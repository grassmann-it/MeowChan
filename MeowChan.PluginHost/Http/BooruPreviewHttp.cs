using System.Net;
using System.Net.Http.Headers;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

/// <summary>
/// Preview bytes of a booru post-search source (the small provider thumbnail shown next to a search result).
/// MeowChan has no other remote preview path: chan thumbnails are generated from local media. This is the
/// minimal, separately bounded path for exactly one purpose, following the B1 metadata rules:
/// <list type="bullet">
/// <item>bodyless GET only, absolute https URL on a host the source declares with <see cref="HttpHostPurpose.PreviewMedia"/>,
/// default port, no user info, no fragment;</item>
/// <item>no redirects: a 3xx (or a transport that followed one) is simply "no preview";</item>
/// <item>no credentials: the transport must carry no Authorization/Cookie defaults, the request sends only the
/// product User-Agent, an image Accept header and <c>Accept-Encoding: identity</c>;</item>
/// <item>at most <see cref="MaximumPreviewBytes"/> per body (declared or streamed), a 15 s response deadline, at most
/// <see cref="MaximumConcurrentRequests"/> requests at once, and only bytes that start like JPEG, PNG, GIF or WebP;</item>
/// <item>no retry and no provider cooldown interaction; any failure is "no preview" and the caller shows a placeholder.</item>
/// </list>
/// A small in-memory cache keeps paging back and forth from fetching the same thumbnail twice. Nothing is stored on disk.
/// </summary>
public sealed class BooruPreviewHttp : IDisposable
{
    public const int MaximumPreviewBytes = 1024 * 1024;
    public const int MaximumConcurrentRequests = 2;
    public const int MaximumCachedPreviews = 120;
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _transport;
    private readonly HashSet<string> _hosts;
    private readonly string _userAgent;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(MaximumConcurrentRequests, MaximumConcurrentRequests);
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, byte[] Bytes)>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, byte[] Bytes)> _order = new();
    private int _requests;
    private volatile bool _disposed;

    /// <param name="transport">Borrowed transport; it must not follow redirects and must not attach credentials.</param>
    /// <param name="source">The post-search source whose declared preview hosts form the allowlist.</param>
    public BooruPreviewHttp(HttpClient transport, SourceDescription source, string? productVersion = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(source);
        _hosts = source.HttpHosts.Where(host => host.Purpose.HasFlag(HttpHostPurpose.PreviewMedia))
            .Select(host => host.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _userAgent = BooruUserAgent.Build(productVersion ?? BooruUserAgent.ProductVersion());
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of preview requests that reached the transport (diagnostics and tests).</summary>
    public int RequestCount => Volatile.Read(ref _requests);

    /// <summary>True when <paramref name="url"/> may be requested at all.</summary>
    public bool IsAllowed(Uri? url) =>
        url is { IsAbsoluteUri: true } && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0
        && url.Fragment.Length == 0 && url.IsDefaultPort && _hosts.Contains(url.IdnHost);

    /// <summary>
    /// The preview bytes, or null when the URL is not allowed or the provider did not deliver a small image.
    /// Only the caller's cancellation is thrown.
    /// </summary>
    public async Task<byte[]?> LoadAsync(Uri? url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !IsAllowed(url) || HasForbiddenTransportDefaults()) return null;
        string key = url!.AbsoluteUri;
        if (TryGetCached(key, out byte[]? cached)) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A preview queued behind the gate may have waited across disposal: it must not start a request.
            if (_disposed) return null;
            if (TryGetCached(key, out cached)) return cached;
            using CancellationTokenSource deadline = new(ResponseTimeout, _clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            byte[]? bytes = await FetchAsync(url, linked.Token).ConfigureAwait(false);
            if (bytes != null) Remember(key, bytes);
            return bytes;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException
            or ObjectDisposedException) { return null; }
        finally { _gate.Release(); }
    }

    private async Task<byte[]?> FetchAsync(Uri url, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/gif"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (_disposed) return null;
        Interlocked.Increment(ref _requests);
        using HttpResponseMessage response = await _transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if ((response.RequestMessage?.RequestUri ?? url) != url) return null; // the transport followed a redirect
        if (response.StatusCode != HttpStatusCode.OK) return null;
        HttpContent content = response.Content;
        if (content.Headers.ContentLength is long length && (length <= 0 || length > MaximumPreviewBytes)) return null;
        if (content.Headers.ContentEncoding.Any(encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
            return null;
        await using Stream body = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await body.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumPreviewBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        byte[] bytes = buffer.ToArray();
        return LooksLikeImage(bytes) ? bytes : null;
    }

    /// <summary>JPEG, PNG, GIF or WebP signature; anything else never reaches an image decoder.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
        || bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
        || bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))
        || bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);

    private bool HasForbiddenTransportDefaults() =>
        _transport.DefaultRequestHeaders.Authorization is not null
        || _transport.DefaultRequestHeaders.Contains("Cookie")
        || _transport.DefaultRequestHeaders.Contains("Proxy-Authorization");

    private bool TryGetCached(string key, out byte[]? bytes)
    {
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(key, out LinkedListNode<(string Url, byte[] Bytes)>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
        }
        bytes = null;
        return false;
    }

    private void Remember(string key, byte[] bytes)
    {
        lock (_cacheSync)
        {
            // A request that was in flight at disposal must not repopulate the cleared cache.
            if (_disposed || _cache.ContainsKey(key)) return;
            _cache[key] = _order.AddFirst((key, bytes));
            while (_cache.Count > MaximumCachedPreviews && _order.Last is { } last)
            {
                _order.RemoveLast();
                _cache.Remove(last.Value.Url);
            }
        }
    }

    /// <summary>
    /// Idempotent. Waiting previews return null without a request; requests already in flight finish on the borrowed
    /// transport, which is not disposed here, and their bytes are not cached.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        lock (_cacheSync)
        {
            _cache.Clear();
            _order.Clear();
        }
    }
}
