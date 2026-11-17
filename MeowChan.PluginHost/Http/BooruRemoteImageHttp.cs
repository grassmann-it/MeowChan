using System.Net;
using System.Net.Http.Headers;
using MeowChan.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

/// <summary>What one remote-image load ended with. Only <see cref="Loaded"/> carries bytes.</summary>
internal enum BooruRemoteImageStatus
{
    Loaded,
    /// <summary>Not a Preview/Sample role, not an https URL on a declared preview host, or a transport carrying credentials.</summary>
    NotAllowed,
    /// <summary>The media fence is up after a provider 429/503; nothing was requested. See <see cref="BooruRemoteImageResult.RetryAt"/>.</summary>
    Fenced,
    /// <summary>A 3xx, or a transport that followed one. Redirects are never followed.</summary>
    Redirected,
    /// <summary>Declared or streamed body above the role's byte limit; the rest of the body was not read.</summary>
    TooLarge,
    /// <summary>Non-image Content-Type, an encoded body, or bytes that do not start like JPEG, PNG, GIF or WebP.</summary>
    WrongType,
    /// <summary>Any other status, transport error or response deadline.</summary>
    Failed,
    /// <summary>The path was disposed (composition teardown) before or while the request ran.</summary>
    Unavailable,
}

/// <summary>
/// One load result. <see cref="Bytes"/> may be shared with the in-memory cache: callers decode it and must not modify it.
/// </summary>
internal readonly record struct BooruRemoteImageResult(BooruRemoteImageStatus Status, byte[]? Bytes = null,
    bool FromCache = false, DateTimeOffset? RetryAt = null, int? HttpStatus = null)
{
    internal static readonly BooruRemoteImageResult NotAllowed = new(BooruRemoteImageStatus.NotAllowed);
    internal static readonly BooruRemoteImageResult Unavailable = new(BooruRemoteImageStatus.Unavailable);
}

/// <summary>
/// User-adjustable limits of the remote-image path (settings arrive with Booru B5; until then the defaults apply).
/// Every value is clamped to its documented minimum/maximum by <see cref="Normalized"/>.
/// </summary>
internal sealed record BooruRemoteImageLimits(long PreviewCacheBytes, long SampleCacheBytes, int MaximumSampleBytes,
    int MaximumConcurrentRequests)
{
    private const long MiB = 1024 * 1024;
    internal const long DefaultPreviewCacheBytes = 32 * MiB, MinimumPreviewCacheBytes = 0, MaximumPreviewCacheBytes = 256 * MiB;
    internal const long DefaultSampleCacheBytes = 128 * MiB, MinimumSampleCacheBytes = 0, MaximumSampleCacheBytes = 1024 * MiB;
    internal const int DefaultSampleBytes = (int)(8 * MiB), MinimumSampleBytes = (int)MiB, MaximumSampleBytesCeiling = (int)(32 * MiB);
    internal const int DefaultConcurrentRequests = 2, MinimumConcurrentRequests = 1, MaximumConcurrentRequestsCeiling = 4;

    internal static BooruRemoteImageLimits Default { get; } = new(DefaultPreviewCacheBytes, DefaultSampleCacheBytes,
        DefaultSampleBytes, DefaultConcurrentRequests);

    internal BooruRemoteImageLimits Normalized() => new(
        Math.Clamp(PreviewCacheBytes, MinimumPreviewCacheBytes, MaximumPreviewCacheBytes),
        Math.Clamp(SampleCacheBytes, MinimumSampleCacheBytes, MaximumSampleCacheBytes),
        Math.Clamp(MaximumSampleBytes, MinimumSampleBytes, MaximumSampleBytesCeiling),
        Math.Clamp(MaximumConcurrentRequests, MinimumConcurrentRequests, MaximumConcurrentRequestsCeiling));
}

/// <summary>
/// The host's only path for remote booru image bytes: the provider thumbnail (<see cref="BooruMediaRole.Preview"/>) next to a
/// search result and the provider sample (<see cref="BooruMediaRole.Sample"/>) of an opened post. Originals never use this
/// path; they go through the verified download queue. The plugin is never involved in media bytes. Rules:
/// <list type="bullet">
/// <item>bodyless GET only, absolute https URL on a host the source declares with <see cref="HttpHostPurpose.PreviewMedia"/>
/// (the SDK validates both Preview and Sample URLs against that purpose, <c>Validation.cs</c>), default port, no user info,
/// no fragment;</item>
/// <item>no redirects: a 3xx, or a transport that followed one, is <see cref="BooruRemoteImageStatus.Redirected"/>;</item>
/// <item>no credentials: a transport with Authorization/Cookie/Proxy-Authorization or any other non-basic default header is
/// refused; the request sends only the product User-Agent, an image Accept header and <c>Accept-Encoding: identity</c>;</item>
/// <item>a hard per-body byte limit (preview <see cref="MaximumPreviewBytes"/>, sample from the limits), checked on the declared
/// length and while streaming; Content-Type, when present, must be JPEG, PNG, GIF or WebP, and the bytes must start like one
/// of those. The host never decodes: decoding stays in the UI layer;</item>
/// <item>one concurrency gate shared by both roles; identical concurrent loads share one request;</item>
/// <item>a media fence: a 429 or 503 stops every further preview/sample request of the source for max(60 s, Retry-After),
/// capped by the configured provider cooldown maximum, and is reported through <see cref="FenceRaised"/>. No retries;</item>
/// <item>cancellation really aborts: when the last caller of a request cancels, the request and its body read are
/// cancelled; disposal aborts queued and running requests;</item>
/// <item>in-memory LRU caches bounded by bytes per role, with <see cref="Clear"/>. Nothing is ever written to disk.</item>
/// </list>
/// </summary>
internal sealed class BooruRemoteImageHttp : IDisposable
{
    /// <summary>Fixed technical ceiling for one preview body (documented, not a setting).</summary>
    internal const int MaximumPreviewBytes = 1024 * 1024;
    /// <summary>At most this many neighbour samples are prefetched per call, whatever the caller asks for.</summary>
    internal const int MaximumPrefetch = 3;
    internal static readonly TimeSpan PreviewResponseTimeout = TimeSpan.FromSeconds(15);
    /// <summary>A sample may be several MiB: its whole response (headers and body) shares the original-media header budget.</summary>
    internal static readonly TimeSpan SampleResponseTimeout = RequestPolicyHandler.OverallTimeout;

    private static readonly HashSet<string> BasicDefaultHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Accept", "Accept-Encoding", "Accept-Language", "User-Agent" };
    private static readonly HashSet<string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    { "image/jpeg", "image/png", "image/gif", "image/webp" };

    private readonly HttpClient _transport;
    private readonly string _sourceId;
    private readonly HashSet<string> _hosts;
    private readonly string _userAgent;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan>? _maximumCooldownProvider;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dictionary<(BooruMediaRole Role, string Url), Flight> _flights = new();
    private readonly ByteLru _previews;
    private readonly ByteLru _samples;
    private int _maximumSampleBytes;
    private long _generation;
    private DateTimeOffset _fenceUntil = DateTimeOffset.MinValue;
    private int _requests;
    private volatile bool _disposed;

    /// <param name="transport">Borrowed transport; it must not follow redirects and must not attach credentials. Not disposed here.</param>
    /// <param name="source">The post-search source whose declared preview hosts form the allowlist.</param>
    /// <param name="limits">Cache budgets, sample limit and concurrency; null uses <see cref="BooruRemoteImageLimits.Default"/>.
    /// The concurrency is fixed for this instance; budgets and the sample limit change with <see cref="ApplyLimits"/>.</param>
    /// <param name="productVersion">Product version for the User-Agent; null uses <see cref="BooruUserAgent.ProductVersion"/>.</param>
    /// <param name="timeProvider">Clock for response deadlines and the media fence; null uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="maximumCooldownProvider">The user's provider cooldown maximum (the same one the metadata path uses);
    /// clamped like <see cref="BooruHostHttp"/>'s, null uses its default.</param>
    internal BooruRemoteImageHttp(HttpClient transport, SourceDescription source, BooruRemoteImageLimits? limits = null,
        string? productVersion = null, TimeProvider? timeProvider = null, Func<TimeSpan>? maximumCooldownProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(source);
        _sourceId = source.SourceId;
        _hosts = (source.HttpHosts ?? []).Where(host => host is not null && host.Purpose.HasFlag(HttpHostPurpose.PreviewMedia))
            .Select(host => host.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _userAgent = BooruUserAgent.Build(productVersion ?? BooruUserAgent.ProductVersion());
        _clock = timeProvider ?? TimeProvider.System;
        _maximumCooldownProvider = maximumCooldownProvider;
        BooruRemoteImageLimits applied = (limits ?? BooruRemoteImageLimits.Default).Normalized();
        _gate = new SemaphoreSlim(applied.MaximumConcurrentRequests, applied.MaximumConcurrentRequests);
        MaximumConcurrentRequests = applied.MaximumConcurrentRequests;
        _previews = new ByteLru(applied.PreviewCacheBytes);
        _samples = new ByteLru(applied.SampleCacheBytes);
        _maximumSampleBytes = applied.MaximumSampleBytes;
    }

    /// <summary>Raised (outside any lock) when a 429/503 raises or extends the media fence; the argument is the new end.</summary>
    internal event Action<DateTimeOffset>? FenceRaised;

    internal int MaximumConcurrentRequests { get; }

    /// <summary>Number of image requests that reached the transport (diagnostics and tests).</summary>
    internal int RequestCount => Volatile.Read(ref _requests);

    /// <summary>The end of the media fence while it is up, otherwise null. Expiry never triggers a request.</summary>
    internal DateTimeOffset? FenceUntil
    {
        get
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_sync) return _fenceUntil > now ? _fenceUntil : null;
        }
    }

    /// <summary>Encoded bytes currently cached for <paramref name="role"/> (for the settings' usage line).</summary>
    internal long CachedBytes(BooruMediaRole role)
    {
        lock (_sync) return CacheFor(role)?.Bytes ?? 0;
    }

    internal int MaximumBytes(BooruMediaRole role)
    {
        lock (_sync) return role == BooruMediaRole.Sample ? _maximumSampleBytes : MaximumPreviewBytes;
    }

    /// <summary>Live-applies budgets and the sample limit; a lowered budget trims at once. Concurrency stays as constructed.</summary>
    internal void ApplyLimits(BooruRemoteImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        BooruRemoteImageLimits applied = limits.Normalized();
        lock (_sync)
        {
            _previews.SetBudget(applied.PreviewCacheBytes);
            _samples.SetBudget(applied.SampleCacheBytes);
            _maximumSampleBytes = applied.MaximumSampleBytes;
        }
    }

    /// <summary>
    /// Drops every cached preview and sample. Requests already running still answer their callers, but their bytes are not
    /// cached, so the cache is empty right after this call and stays free of anything requested before it.
    /// </summary>
    internal void Clear()
    {
        lock (_sync)
        {
            _generation++;
            _previews.Clear();
            _samples.Clear();
        }
    }

    /// <summary>True when <paramref name="url"/> may be requested for <paramref name="role"/> at all.</summary>
    internal bool IsAllowed(BooruMediaRole role, Uri? url) =>
        role is BooruMediaRole.Preview or BooruMediaRole.Sample
        && url is { IsAbsoluteUri: true } && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0
        && url.Fragment.Length == 0 && url.IsDefaultPort && _hosts.Contains(url.IdnHost);

    /// <summary>
    /// Loads one preview or sample: from the cache, or with at most one request. Only the caller's cancellation is thrown;
    /// every other outcome is a <see cref="BooruRemoteImageResult"/>.
    /// </summary>
    internal async Task<BooruRemoteImageResult> LoadAsync(BooruMediaRole role, Uri? url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) return BooruRemoteImageResult.Unavailable;
        if (!IsAllowed(role, url) || HasForbiddenTransportDefaults()) return BooruRemoteImageResult.NotAllowed;
        (BooruMediaRole, string) key = (role, url!.AbsoluteUri);

        Flight flight;
        bool start = false;
        lock (_sync)
        {
            if (_disposed) return BooruRemoteImageResult.Unavailable;
            if (CacheFor(role)!.TryGet(key.Item2, out byte[]? cached))
                return new BooruRemoteImageResult(BooruRemoteImageStatus.Loaded, cached, FromCache: true);
            if (_fenceUntil > _clock.GetUtcNow())
                return new BooruRemoteImageResult(BooruRemoteImageStatus.Fenced, RetryAt: _fenceUntil);
            if (!_flights.TryGetValue(key, out flight!) || flight.Abandoned)
            {
                flight = new Flight(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token), _generation);
                _flights[key] = flight;
                start = true;
            }
            flight.Waiters++;
        }
        // Started outside the lock: a synchronously completing transport must not run the whole request under it.
        if (start) _ = RunAsync(flight, key.Item1, url, key.Item2);

        try
        {
            return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { Leave(flight); }
    }

    /// <summary>
    /// Prefetches at most <paramref name="count"/> (clamped to 0..<see cref="MaximumPrefetch"/>) samples of
    /// <paramref name="neighbours"/>, in order and one after another, so a prefetch never occupies more than one gate slot.
    /// Stops at the media fence or disposal. Returns how many samples are now cached or were loaded.
    /// </summary>
    internal async Task<int> PrefetchSamplesAsync(IReadOnlyList<Uri?> neighbours, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        int wanted = Math.Clamp(count, 0, MaximumPrefetch), loaded = 0;
        for (int index = 0; index < neighbours.Count && index < wanted; index++)
        {
            BooruRemoteImageResult result = await LoadAsync(BooruMediaRole.Sample, neighbours[index], cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == BooruRemoteImageStatus.Loaded) loaded++;
            else if (result.Status is BooruRemoteImageStatus.Fenced or BooruRemoteImageStatus.Unavailable) break;
        }
        return loaded;
    }

    private async Task RunAsync(Flight flight, BooruMediaRole role, Uri url, string key)
    {
        BooruRemoteImageResult result = BooruRemoteImageResult.Unavailable;
        bool entered = false;
        try
        {
            await _gate.WaitAsync(flight.Abort.Token).ConfigureAwait(false);
            entered = true;
            int limit;
            lock (_sync)
            {
                // A load queued behind the gate may have waited across disposal, a fence or another load of the same image.
                if (_disposed) return;
                if (CacheFor(role)!.TryGet(key, out byte[]? cached))
                {
                    result = new BooruRemoteImageResult(BooruRemoteImageStatus.Loaded, cached, FromCache: true);
                    return;
                }
                if (_fenceUntil > _clock.GetUtcNow())
                {
                    result = new BooruRemoteImageResult(BooruRemoteImageStatus.Fenced, RetryAt: _fenceUntil);
                    return;
                }
                limit = role == BooruMediaRole.Sample ? _maximumSampleBytes : MaximumPreviewBytes;
            }
            TimeSpan timeout = role == BooruMediaRole.Sample ? SampleResponseTimeout : PreviewResponseTimeout;
            using CancellationTokenSource deadline = new(timeout, _clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(flight.Abort.Token, deadline.Token);
            result = await FetchAsync(url, limit, linked.Token).ConfigureAwait(false);
            if (result.Status == BooruRemoteImageStatus.Loaded) Remember(role, key, result.Bytes!, flight.Generation);
        }
        catch (OperationCanceledException) when (flight.Abort.IsCancellationRequested)
        {
            // Every caller left or the path was disposed: nobody reads this result.
            result = BooruRemoteImageResult.Unavailable;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException
            or InvalidOperationException or ObjectDisposedException)
        {
            result = new BooruRemoteImageResult(BooruRemoteImageStatus.Failed);
        }
        finally
        {
            if (entered) _gate.Release();
            lock (_sync)
            {
                flight.Finished = true;
                if (_flights.TryGetValue((role, key), out Flight? current) && ReferenceEquals(current, flight))
                    _flights.Remove((role, key));
            }
            flight.Completion.TrySetResult(result);
            flight.Abort.Dispose();
        }
    }

    private async Task<BooruRemoteImageResult> FetchAsync(Uri url, int limit, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/gif"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Options.Set(MeteringHandler.SourceKey, _sourceId);
        if (_disposed) return BooruRemoteImageResult.Unavailable;
        Interlocked.Increment(ref _requests);
        using HttpResponseMessage response = await _transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        int status = (int)response.StatusCode;
        if ((response.RequestMessage?.RequestUri ?? url) != url || status is >= 300 and <= 399)
            return new BooruRemoteImageResult(BooruRemoteImageStatus.Redirected, HttpStatus: status);
        if (status is 429 or 503)
        {
            DateTimeOffset until = RaiseFence(ReadRetryAfter(response));
            return new BooruRemoteImageResult(BooruRemoteImageStatus.Fenced, RetryAt: until, HttpStatus: status);
        }
        if (response.StatusCode != HttpStatusCode.OK) return new BooruRemoteImageResult(BooruRemoteImageStatus.Failed, HttpStatus: status);
        HttpContent content = response.Content;
        if (content.Headers.ContentEncoding.Any(encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
            || !IsImageContentType(content.Headers))
            return new BooruRemoteImageResult(BooruRemoteImageStatus.WrongType, HttpStatus: status);
        long? declared = content.Headers.ContentLength;
        if (declared is long length && (length <= 0 || length > limit))
            return new BooruRemoteImageResult(length <= 0 ? BooruRemoteImageStatus.Failed : BooruRemoteImageStatus.TooLarge,
                HttpStatus: status);

        await using Stream body = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        // Read into one buffer of at most limit + 1 bytes: one byte past the limit proves the body too large without reading on.
        byte[] buffer = new byte[(declared is long known ? (int)known : Math.Min(limit, 64 * 1024)) + 1];
        int total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length > limit) return new BooruRemoteImageResult(BooruRemoteImageStatus.TooLarge, HttpStatus: status);
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, (long)limit + 1));
            }
            int read = await body.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > limit) return new BooruRemoteImageResult(BooruRemoteImageStatus.TooLarge, HttpStatus: status);
        }
        if (total == 0 || declared is long expected && total != expected)
            return new BooruRemoteImageResult(BooruRemoteImageStatus.Failed, HttpStatus: status);
        byte[] bytes = total == buffer.Length ? buffer : buffer.AsSpan(0, total).ToArray();
        return LooksLikeImage(bytes)
            ? new BooruRemoteImageResult(BooruRemoteImageStatus.Loaded, bytes, HttpStatus: status)
            : new BooruRemoteImageResult(BooruRemoteImageStatus.WrongType, HttpStatus: status);
    }

    /// <summary>JPEG, PNG, GIF or WebP signature; anything else never reaches an image decoder.</summary>
    internal static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
        || bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
        || bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))
        || bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);

    /// <summary>
    /// A declared Content-Type must be one of the four image types; an unparseable one is refused. A missing one is left to
    /// the signature check. A declared type that differs from the signature is accepted: the decoder sniffs the bytes anyway.
    /// </summary>
    private static bool IsImageContentType(HttpContentHeaders headers)
    {
        if (!headers.NonValidated.Contains("Content-Type")) return true;
        return headers.ContentType?.MediaType is string mediaType && ImageMediaTypes.Contains(mediaType);
    }

    private bool HasForbiddenTransportDefaults() =>
        _transport.DefaultRequestHeaders.Authorization is not null
        || _transport.DefaultRequestHeaders.Any(header => !BasicDefaultHeaders.Contains(header.Key));

    private DateTimeOffset RaiseFence(TimeSpan? requested)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset minimum = AddSaturating(now, BooruHostHttp.MinimumCooldown);
        DateTimeOffset maximum = AddSaturating(now, MaximumCooldown());
        DateTimeOffset wanted = requested is TimeSpan delay ? AddSaturating(now, delay) : minimum;
        DateTimeOffset until = wanted < minimum ? minimum : wanted > maximum ? maximum : wanted;
        bool raised;
        lock (_sync)
        {
            raised = until > _fenceUntil;
            if (raised) _fenceUntil = until;
            else until = _fenceUntil;
        }
        if (raised)
        {
            try { FenceRaised?.Invoke(until); }
            catch (Exception) { /* an observer fault must not turn the fence into a failed image */ }
        }
        return until;
    }

    private TimeSpan MaximumCooldown()
    {
        TimeSpan value;
        try { value = _maximumCooldownProvider?.Invoke() ?? BooruHostHttp.DefaultMaximumCooldown; }
        catch { return BooruHostHttp.DefaultMaximumCooldown; }
        return value < BooruHostHttp.MinimumConfiguredCooldown ? BooruHostHttp.MinimumConfiguredCooldown
            : value > BooruHostHttp.MaximumConfiguredCooldown ? BooruHostHttp.MaximumConfiguredCooldown : value;
    }

    /// <summary>
    /// Retry-After as a delay from now. Provider input is untrusted: an unparseable value means only the minimum applies;
    /// delta-seconds saturate so a huge valid value hits the cap instead of being mistaken for garbage (same rules as the
    /// metadata path in <see cref="BooruHostHttp"/>).
    /// </summary>
    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        IEnumerable<string>? values;
        try
        {
            if (!response.Headers.TryGetValues("Retry-After", out values)) return null;
        }
        catch (FormatException) { return null; }
        foreach (string raw in values)
        {
            string text = raw.Trim();
            if (text.Length == 0) continue;
            if (text.All(char.IsAsciiDigit))
            {
                long seconds = 0, ceiling = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond;
                foreach (char character in text)
                {
                    int digit = character - '0';
                    if (seconds > (ceiling - digit) / 10) { seconds = ceiling; break; }
                    seconds = seconds * 10 + digit;
                }
                return TimeSpan.FromSeconds(seconds);
            }
            if (DateTimeOffset.TryParseExact(text, "r", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset date))
            {
                TimeSpan left = date - _clock.GetUtcNow();
                return left > TimeSpan.Zero ? left : null;
            }
        }
        return null;
    }

    private static DateTimeOffset AddSaturating(DateTimeOffset now, TimeSpan delay) =>
        delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;

    private ByteLru? CacheFor(BooruMediaRole role) => role switch
    {
        BooruMediaRole.Preview => _previews,
        BooruMediaRole.Sample => _samples,
        _ => null,
    };

    private void Remember(BooruMediaRole role, string key, byte[] bytes, long generation)
    {
        lock (_sync)
        {
            // A request that was in flight at disposal or at a Clear() must not repopulate the cleared cache.
            if (_disposed || generation != _generation) return;
            CacheFor(role)!.Add(key, bytes);
        }
    }

    private void Leave(Flight flight)
    {
        bool abandon;
        lock (_sync)
        {
            flight.Waiters--;
            abandon = flight.Waiters == 0 && !flight.Finished;
            if (abandon) flight.Abandoned = true;
        }
        if (!abandon) return;
        // The last caller left: abort the request, its body read or its wait at the gate. A later load starts a new flight.
        try { flight.Abort.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Idempotent. Aborts queued and running requests (their callers get <see cref="BooruRemoteImageStatus.Unavailable"/>),
    /// drops the caches and refuses later loads. The borrowed transport is not disposed.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _previews.Clear();
            _samples.Clear();
        }
        try { _lifetime.Cancel(); }
        catch (AggregateException) { }
        // _lifetime and _gate stay undisposed: a flight finishing after this call still links to / releases them.
    }

    private sealed class Flight(CancellationTokenSource abort, long generation)
    {
        internal CancellationTokenSource Abort { get; } = abort;
        internal long Generation { get; } = generation;
        internal TaskCompletionSource<BooruRemoteImageResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
        internal bool Finished;
        internal bool Abandoned;
    }

    /// <summary>Least-recently-used byte cache; callers hold the owner's lock.</summary>
    private sealed class ByteLru(long budget)
    {
        private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, byte[] Bytes)> _order = new();
        private long _budget = budget;

        internal long Bytes { get; private set; }

        internal bool TryGet(string key, out byte[]? bytes)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<(string Key, byte[] Bytes)>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
            bytes = null;
            return false;
        }

        internal void Add(string key, byte[] bytes)
        {
            // An entry above the whole budget (or a zero budget) is never cached and never evicts the others.
            if (_entries.ContainsKey(key) || bytes.Length > _budget) return;
            _entries[key] = _order.AddFirst((key, bytes));
            Bytes += bytes.Length;
            Trim();
        }

        internal void SetBudget(long budget)
        {
            _budget = budget;
            Trim();
        }

        internal void Clear()
        {
            _entries.Clear();
            _order.Clear();
            Bytes = 0;
        }

        private void Trim()
        {
            while (Bytes > _budget && _order.Last is { } last)
            {
                _order.RemoveLast();
                _entries.Remove(last.Value.Key);
                Bytes -= last.Value.Bytes.Length;
            }
        }
    }
}
