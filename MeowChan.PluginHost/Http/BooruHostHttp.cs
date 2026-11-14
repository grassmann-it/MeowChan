using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using MeowChan.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

/// <summary>
/// Bounded metadata transport for post-search sources. This path intentionally does not
/// reuse <see cref="RequestPolicyHandler"/>: booru retries, pacing and job budgets differ
/// from the ordinary chan policy.
/// </summary>
public sealed class BooruHostHttp : IDisposable
{
    public const int MaximumWireBytes = 2 * 1024 * 1024;
    public const int MaximumDecodedBytes = 4 * 1024 * 1024;
    public const int MaximumSendsPerJob = BooruJob.MaximumSends;
    public const int MaximumSearchPage = MeowChan.Booru.DanbooruTagQuery.MaximumSearchPages;
    public const int MaximumSearchLimit = MeowChan.Booru.DanbooruTagQuery.MaximumSearchPageSize;
    public static readonly TimeSpan MinimumStartSpacing = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumJobLifetime = BooruJob.MaximumLifetime;
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MinimumCooldown = TimeSpan.FromSeconds(60);
    /// <summary>The default maximum for provider Retry-After cooldowns. The user-facing setting is bounded below/above.
    /// </summary>
    internal static readonly TimeSpan DefaultMaximumCooldown = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MinimumConfiguredCooldown = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MaximumConfiguredCooldown = TimeSpan.FromHours(168);
    /// <summary>Danbooru post ids fit a signed 64-bit integer: at most 18 canonical decimal digits are accepted.</summary>
    private const int MaximumPostIdDigits = 18;

    private readonly HttpClient _transport;
    private readonly bool _disposeTransport;
    private readonly TimeProvider _clock;
    private readonly string _userAgent;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;
    private readonly Func<TimeSpan>? _maximumCooldownProvider;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceState> _states = new(StringComparer.Ordinal);

    // Keep the original public constructor shape for already compiled PluginHost consumers. The
    // configurable cap is an internal host wiring detail, not a new SDK surface.
    public BooruHostHttp(HttpClient transport, RequestPolicyState policy, IReadOnlyList<SourceDescription> sources,
        string? danbooruUserId = null, string? productVersion = null, bool disposeTransport = false)
        : this(transport, policy, sources, null, danbooruUserId, productVersion, disposeTransport)
    {
    }

    internal BooruHostHttp(HttpClient transport, RequestPolicyState policy, IReadOnlyList<SourceDescription> sources,
        Func<TimeSpan>? maximumCooldownProvider, string? danbooruUserId = null,
        string? productVersion = null, bool disposeTransport = false)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(policy);
        _clock = policy.TimeProvider;
        _disposeTransport = disposeTransport;
        _maximumCooldownProvider = maximumCooldownProvider;
        _sources = (sources ?? throw new ArgumentNullException(nameof(sources)))
            .Where(source => source is not null)
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        _userAgent = BooruUserAgent.Build(productVersion ?? BooruUserAgent.ProductVersion(), danbooruUserId);
    }

    internal IReadOnlyDictionary<string, SourceDescription> Sources => _sources;
    internal TimeProvider TimeProvider => _clock;

    internal async Task<HostHttpResult> SendAsync(HostHttpRequest request, SourceDescription source,
        BooruJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null || !_sources.TryGetValue(source.SourceId, out SourceDescription? declared)
            || !ReferenceEquals(source, declared) && source != declared)
            return Failure("booru-source", "The booru source is not declared by this host.");
        if (!source.Capabilities.HasFlag(SourceCapabilities.PostSearch))
            return Failure("booru-source", "The source does not declare post search.");
        if (!job.IsOpen) return Failure("booru-job", "The booru job is not live.");
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateHttpRequest(request, source);
        if (issues.Count > 0) return Failure("http-request", issues[0].Message);
        if (request.Method != HostHttpMethod.Get)
            return Failure("http-request", "Booru metadata accepts GET requests only.");
        if (!IsAllowedEndpoint(request.Url, source))
            return Failure("http-request", "The booru endpoint is not on the provider allowlist.");
        if (HasForbiddenTransportDefaults() || HasForbiddenRequestHeaders(request.Headers))
            return Failure("http-request", "The booru transport contains host-owned credentials.");
        if (job.IsExpired) return Failure("booru-budget", "The booru job budget is exhausted.");

        SourceState state = _states.GetOrAdd(source.SourceId, static _ => new SourceState());
        // Every wait and attempt observes both the caller and the job lifetime. Closing the
        // job (or reaching its deadline) therefore aborts a queued send-gate admission,
        // provider spacing, retry delays, the header wait and body reads alike.
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.Token);
        CancellationToken token = abort.Token;
        bool entered = false;
        try
        {
            // ResetCooldown serializes with the whole source operation. A caller that acquired the
            // semaphore before reset is allowed to finish; a caller that races reset observes the
            // reset barrier and cannot start an HTTP request until the fence has been cleared.
            while (true)
            {
                await state.SendGate.WaitAsync(token).ConfigureAwait(false);
                entered = true;
                Task? reset = null;
                lock (state.Gate)
                    if (state.Resetting) reset = state.ResetCompleted!.Task;
                if (reset is null) break;
                state.SendGate.Release();
                entered = false;
                await reset.WaitAsync(token).ConfigureAwait(false);
            }
            while (true)
            {
                if (JobEnded(job) is HostHttpFailure ended) return ended;
                DateTimeOffset now = _clock.GetUtcNow();
                DateTimeOffset cooldownUntil;
                // CooldownUntil is a 16-byte struct written under Gate: never read it torn.
                lock (state.Gate)
                {
                    DateTimeOffset cap = AddSaturating(now, GetMaximumCooldown());
                    if (state.CooldownUntil > cap) state.CooldownUntil = cap;
                    cooldownUntil = state.CooldownUntil;
                }
                if (cooldownUntil > now)
                    return new HostHttpDeferred(cooldownUntil, "booru-cooldown");
                if (now >= job.Deadline) return Failure("booru-budget", "The booru job budget is exhausted.");

                DateTimeOffset due = state.NextStart;
                if (due > now)
                {
                    DateTimeOffset waitUntil = due < job.Deadline ? due : job.Deadline;
                    await DelayUntilAsync(waitUntil, token).ConfigureAwait(false);
                    continue;
                }

                if (!job.TryReserveSend())
                    return JobEnded(job) ?? Failure("booru-budget", "The booru job send budget is exhausted.");
                state.NextStart = AddSaturating(_clock.GetUtcNow(), MinimumStartSpacing);
                return await SendWithRetryAsync(request, source, job, state, cancellationToken, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && job.Token.IsCancellationRequested)
        {
            return JobEnded(job) ?? Failure("booru-job", "The booru job is not live.");
        }
        finally
        {
            if (entered) state.SendGate.Release();
        }
    }

    /// <summary>The terminal failure of a closed, expired or operation-cancelled job; null while it is live.</summary>
    private static HostHttpFailure? JobEnded(BooruJob job, int? status = null)
    {
        if (!job.IsOpen) return Failure("booru-job", "The booru job is not live.", status);
        if (job.IsExpired) return Failure("booru-budget", "The booru job budget is exhausted.", status);
        if (job.Token.IsCancellationRequested) return Failure("booru-job", "The booru job is not live.", status);
        return null;
    }

    internal bool TryGetCooldown(string sourceId, out DateTimeOffset retryAt)
    {
        if (_states.TryGetValue(sourceId, out SourceState? state))
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (state.Gate)
            {
                DateTimeOffset cap = AddSaturating(now, GetMaximumCooldown());
                if (state.CooldownUntil > cap) state.CooldownUntil = cap;
                if (state.CooldownUntil > now)
                {
                    retryAt = state.CooldownUntil;
                    return true;
                }
            }
        }
        retryAt = default;
        return false;
    }

    /// <summary>
    /// Starts a host-fence reset transaction. The source gate is held until the caller either commits
    /// after durable scheduler persistence or disposes to abort. This ordering is intentional: a request
    /// cannot start in the gap between the durable reset and transport-fence release.
    /// </summary>
    internal CooldownReset? BeginCooldownReset(string sourceId)
    {
        if (!_sources.ContainsKey(sourceId)) return null;
        SourceState state = _states.GetOrAdd(sourceId, static _ => new SourceState());

        while (true)
        {
            Task? completion;
            long resetVersion;
            lock (state.Gate)
            {
                if (!state.Resetting)
                {
                    state.Resetting = true;
                    state.ResetCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    // Capture the version at barrier admission. A late 429 from an already running
                    // request increments this version while we wait and must survive the reset.
                    resetVersion = state.CooldownVersion;
                    completion = null;
                }
                else
                {
                    completion = state.ResetCompleted!.Task;
                    resetVersion = 0;
                }
            }
            if (completion is not null)
            {
                completion.GetAwaiter().GetResult();
                continue;
            }

            bool entered = false;
            try
            {
                // Resetting is already visible under Gate, so a waiter that happens to acquire the
                // semaphore before this call does so only to observe the barrier and wait.
                state.SendGate.Wait();
                entered = true;
                return new CooldownReset(state, resetVersion);
            }
            catch
            {
                lock (state.Gate)
                {
                    state.Resetting = false;
                    state.ResetCompleted!.TrySetResult();
                }
                if (entered) state.SendGate.Release();
                throw;
            }
        }
    }

    /// <summary>Clears only the in-memory provider fence for one declared source. Pacing remains intact.</summary>
    internal bool ResetCooldown(string sourceId)
    {
        CooldownReset? reset = BeginCooldownReset(sourceId);
        if (reset is null) return false;
        using (reset) return reset.Commit();
    }

    internal TimeSpan GetMaximumCooldown()
    {
        TimeSpan value;
        try { value = _maximumCooldownProvider?.Invoke() ?? DefaultMaximumCooldown; }
        catch { return DefaultMaximumCooldown; }
        return value < MinimumConfiguredCooldown ? MinimumConfiguredCooldown
            : value > MaximumConfiguredCooldown ? MaximumConfiguredCooldown : value;
    }

    private async Task<HostHttpResult> SendWithRetryAsync(HostHttpRequest request, SourceDescription source,
        BooruJob job, SourceState state, CancellationToken cancellationToken, CancellationToken abort)
    {
        bool retried = false;
        while (true)
        {
            HttpResponseMessage? response = null;
            using AttemptWindow window = new(_clock, abort,
                Min(AddSaturating(_clock.GetUtcNow(), ResponseTimeout), job.Deadline));
            try
            {
                response = await SendAttemptAsync(request, window.Token).ConfigureAwait(false);
                // A late transport completion must observe the response deadline as well as
                // job/caller cancellation before any status handling, retry or body read.
                window.Token.ThrowIfCancellationRequested();
                if (response.RequestMessage?.RequestUri is Uri actual && actual != request.Url)
                    return Failure("http-redirect", "Metadata redirects are not followed.");

                int status = (int)response.StatusCode;
                // Decide on redirects, cooldowns and retries before any body byte is read.
                // A 304 answers the plugin's own conditional request: it is not a redirect and
                // carries no body. An unconditional 304 stays a terminal 3xx like any other.
                if (status == 304 && IsConditional(request))
                    return new HostHttpResponse(status, request.Url, ReadHeaders(response), [],
                        new SourceValidators(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified));
                if (status is >= 300 and <= 399)
                    return Failure("http-redirect", "Metadata redirects are not followed.", status);
                if (status == 429)
                {
                    DateTimeOffset cooldown = CooldownUntil(ReadRetryAfter(response), _clock.GetUtcNow(), GetMaximumCooldown());
                    lock (state.Gate)
                    {
                        state.CooldownVersion++;
                        if (cooldown > state.CooldownUntil) state.CooldownUntil = cooldown;
                    }
                    HostHttpDeferred deferred = new(cooldown, "booru-cooldown", status);
                    job.StopForCooldown(deferred);
                    return deferred;
                }
                if (status is 502 or 503 && !retried)
                {
                    retried = true;
                    DateTimeOffset retryAt = RetryAt(ReadRetryAfter(response), _clock.GetUtcNow(), GetMaximumCooldown());
                    if (retryAt >= job.Deadline || job.IsExpired)
                        return Failure("booru-budget", "The booru job budget is exhausted.", status);
                    response.Dispose();
                    response = null;
                    await DelayUntilAsync(retryAt, abort).ConfigureAwait(false);
                    if (JobEnded(job, status) is HostHttpFailure ended) return ended;
                    if (!job.TryReserveSend())
                        return JobEnded(job, status) ?? Failure("booru-budget", "The booru job send budget is exhausted.", status);
                    state.NextStart = AddSaturating(_clock.GetUtcNow(), MinimumStartSpacing);
                    continue;
                }

                byte[] body = await ReadBodyAsync(response.Content, window, abort).ConfigureAwait(false);
                return new HostHttpResponse(status, request.Url,
                    ReadHeaders(response, status is 502 or 503 ? GetMaximumCooldown() : null, _clock.GetUtcNow()), body,
                    new SourceValidators(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (job.Token.IsCancellationRequested && exception is OperationCanceledException
                or BooruAttemptTimeoutException or HttpRequestException or IOException)
            {
                // Close/deadline/operation end aborted this attempt: never classify it as a
                // provider failure and never retry it.
                return JobEnded(job) ?? Failure("booru-job", "The booru job is not live.");
            }
            catch (BooruAttemptTimeoutException exception)
            {
                if (job.IsExpired) return Failure("booru-budget", "The booru job budget is exhausted.", exception: exception);
                return Failure("http-timeout", "The booru metadata response exceeded its 15-second deadline.", exception: exception);
            }
            catch (OperationCanceledException exception)
            {
                if (job.IsExpired) return Failure("booru-budget", "The booru job budget is exhausted.", exception: exception);
                return Failure("http-timeout", "The booru metadata response exceeded its 15-second deadline.", exception: exception);
            }
            catch (BooruBodyTooLargeException exception)
            {
                return Failure("metadata-too-large", "The booru metadata response exceeds its bounded body limit.", exception: exception);
            }
            catch (BooruEncodingException exception)
            {
                return Failure("http-encoding", "The booru metadata response encoding is not supported.", exception: exception);
            }
            catch (InvalidDataException exception)
            {
                return Failure("http-encoding", "The booru metadata response encoding is not supported.", exception: exception);
            }
            catch (HttpRequestException) when (!retried && !IsTerminalStatus(response))
            {
                retried = true;
                if (await DelayTransportRetryAsync(job, state, abort).ConfigureAwait(false) is HostHttpFailure stop)
                    return stop;
                continue;
            }
            catch (HttpRequestException exception)
            {
                return Failure("http-network", "The booru metadata request failed.", response is null ? null : (int)response.StatusCode, exception: exception);
            }
            catch (IOException) when (!retried && !IsTerminalStatus(response))
            {
                retried = true;
                if (await DelayTransportRetryAsync(job, state, abort).ConfigureAwait(false) is HostHttpFailure stop)
                    return stop;
                continue;
            }
            catch (IOException exception)
            {
                return Failure("http-network", "The booru metadata response could not be read.", response is null ? null : (int)response.StatusCode, exception: exception);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    /// <summary>Waits for the single transport retry; returns the stop failure when the job cannot send again.</summary>
    private async Task<HostHttpFailure?> DelayTransportRetryAsync(BooruJob job, SourceState state, CancellationToken abort)
    {
        DateTimeOffset retryAt = AddSaturating(_clock.GetUtcNow(), RetryDelay);
        if (retryAt >= job.Deadline || job.IsExpired)
            return Failure("booru-budget", "The booru job budget is exhausted.");
        // A cancellation here propagates to SendAsync, which maps a job end to its failure.
        await DelayUntilAsync(retryAt, abort).ConfigureAwait(false);
        if (JobEnded(job) is HostHttpFailure ended) return ended;
        if (!job.TryReserveSend())
            return JobEnded(job) ?? Failure("booru-budget", "The booru job budget is exhausted.");
        state.NextStart = AddSaturating(_clock.GetUtcNow(), MinimumStartSpacing);
        return null;
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(HostHttpRequest request, CancellationToken token)
    {
        // The send was reserved under the job gate; a Close racing that reservation has
        // already cancelled this token, so the transport is never entered afterwards.
        token.ThrowIfCancellationRequested();
        using HttpRequestMessage message = new(HttpMethod.Get, request.Url);
        message.Headers.UserAgent.Clear();
        message.Headers.UserAgent.ParseAdd(_userAgent);
        message.Headers.AcceptEncoding.Clear();
        message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        message.Options.Set(MeteringHandler.SourceKey, request.SourceId);
        if (request.Headers is not null)
            foreach ((string name, string value) in request.Headers)
                message.Headers.TryAddWithoutValidation(name, value);
        if (request.Validators?.EntityTag is string etag)
            message.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (request.Validators?.LastModified is DateTimeOffset modified)
            message.Headers.IfModifiedSince = modified;
        return await _transport.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, AttemptWindow window,
        CancellationToken abort)
    {
        if (content.Headers.ContentLength is long length && length > MaximumWireBytes)
            throw new BooruBodyTooLargeException();

        await using Stream raw = await content.ReadAsStreamAsync(window.Token).ConfigureAwait(false);
        using MemoryStream wire = new();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int read;
            try { read = await raw.ReadAsync(chunk, window.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (abort.IsCancellationRequested) { throw; }
            catch (OperationCanceledException exception) when (window.Expired) { throw new BooruAttemptTimeoutException(exception); }
            if (read == 0) break;
            if (wire.Length + read > MaximumWireBytes) throw new BooruBodyTooLargeException();
            await wire.WriteAsync(chunk.AsMemory(0, read), window.Token).ConfigureAwait(false);
        }

        Stream decoded = CreateDecoder(new MemoryStream(wire.ToArray(), writable: false), content.Headers.ContentEncoding);
        try
        {
            using (MemoryStream output = new())
            {
                while (true)
                {
                    int read;
                    try { read = await decoded.ReadAsync(chunk, window.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (abort.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException exception) when (window.Expired) { throw new BooruAttemptTimeoutException(exception); }
                    if (read == 0) break;
                    if (output.Length + read > MaximumDecodedBytes) throw new BooruBodyTooLargeException();
                    await output.WriteAsync(chunk.AsMemory(0, read), window.Token).ConfigureAwait(false);
                }
                return output.ToArray();
            }
        }
        catch (BooruBodyTooLargeException) { throw; }
        catch (BooruEncodingException) { throw; }
        catch (InvalidDataException exception) { throw new BooruEncodingException(exception); }
        // BrotliStream reports corrupt input as InvalidOperationException, not InvalidDataException.
        catch (InvalidOperationException exception) { throw new BooruEncodingException(exception); }
        finally
        {
            // The fully built chain is owned here; CreateDecoder owns failed construction.
            decoded.Dispose();
        }
    }

    /// <summary>Takes ownership of the encoded stream, including partially constructed chains on failure.</summary>
    internal static Stream CreateDecoder(Stream decoded, ICollection<string> encodings)
    {
        try
        {
            if (encodings.Count > 2) throw new BooruEncodingException();
            foreach (string encoding in encodings.Reverse())
                decoded = encoding.ToLowerInvariant() switch
                {
                    "identity" => decoded,
                    "gzip" => new GZipStream(decoded, CompressionMode.Decompress),
                    "deflate" => new DeflateStream(decoded, CompressionMode.Decompress),
                    "br" => new BrotliStream(decoded, CompressionMode.Decompress),
                    _ => throw new BooruEncodingException()
                };
            return decoded;
        }
        catch
        {
            decoded.Dispose();
            throw;
        }
    }

    private static bool IsAllowedEndpoint(Uri uri, SourceDescription source)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || !uri.IsDefaultPort) return false;
        // Fail closed: only a source with a host-owned endpoint profile may send. A PostSearch source
        // without one (a new or third-party plugin) is refused until its profile exists here.
        if (!string.Equals(source.SourceId, "danbooru", StringComparison.Ordinal)) return false;

        string path = uri.AbsolutePath;
        if (path == "/posts.json") return IsValidSearchQuery(uri.Query);
        if (!path.StartsWith("/posts/", StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal)
            || uri.Query.Length != 0) return false;
        return IsCanonicalPostId(path[7..^5]);
    }

    private static bool IsCanonicalPostId(string id) =>
        id.Length is > 0 and <= MaximumPostIdDigits && id[0] != '0' && id.All(char.IsAsciiDigit);

    private static bool IsConditional(HostHttpRequest request) =>
        request.Validators?.EntityTag is not null || request.Validators?.LastModified is not null;

    private static bool IsValidSearchQuery(string query)
    {
        if (query.Length == 0) return true;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string part in query[1..].Split('&'))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) return false;
            string name, value;
            try
            {
                name = Uri.UnescapeDataString(part[..equals]);
                value = Uri.UnescapeDataString(part[(equals + 1)..]);
            }
            catch (UriFormatException) { return false; }
            if (!names.Add(name) || name is not ("tags" or "page" or "limit")) return false;
            switch (name)
            {
                case "tags":
                    if (!MeowChan.Booru.DanbooruTagQuery.TryParse(value, out _, out _)) return false;
                    break;
                case "page":
                    // Either a page number 1..5 or Danbooru's before-id cursor "b<post id>" (posts below
                    // that id), checked on the raw text so no escaped form slips through.
                    if (IsBeforeIdPage(part[(equals + 1)..])) break;
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int page)
                        || page is < 1 or > MaximumSearchPage) return false;
                    break;
                default:
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int limit)
                        || limit is < 1 or > MaximumSearchLimit) return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>Exactly <c>b</c> followed by a canonical Danbooru post id (1 to 18 digits, no leading zero).</summary>
    private static bool IsBeforeIdPage(string raw) =>
        raw.Length is >= 2 and <= 19 && raw[0] == 'b' && raw[1] is >= '1' and <= '9'
        && raw.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    private bool HasForbiddenTransportDefaults() =>
        _transport.DefaultRequestHeaders.Authorization is not null
        || _transport.DefaultRequestHeaders.Contains("Cookie")
        || _transport.DefaultRequestHeaders.Contains("Proxy-Authorization");

    private static bool HasForbiddenRequestHeaders(IReadOnlyDictionary<string, string>? headers) =>
        headers is not null && headers.Keys.Any(static name =>
            string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase));

    private IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response, TimeSpan? retryAfterMaximum = null,
        DateTimeOffset? now = null)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "ETag", "Last-Modified", "Content-Type", "Retry-After", "Cache-Control", "Date" })
            if (response.Headers.TryGetValues(name, out IEnumerable<string>? values)
                || response.Content.Headers.TryGetValues(name, out values))
                headers[name] = string.Join(", ", values);
        // A second 502/503 response is deliberately returned to the plugin after the host's one retry.
        // Normalize only a valid value beyond the live cap, otherwise the official plugin's own parser
        // cannot silently turn the configurable 168 h upper bound back into an older hard-coded policy.
        if (retryAfterMaximum is { } maximum && headers.ContainsKey("Retry-After")
            && ReadRetryAfter(response) is { } parsed)
        {
            DateTimeOffset current = now ?? _clock.GetUtcNow();
            DateTimeOffset? requested = RequestedRetryAt(parsed, current);
            DateTimeOffset cap = AddSaturating(current, maximum);
            if (requested is { } due && due > cap)
                headers["Retry-After"] = maximum.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return headers;
    }

    /// <summary>
    /// Reads Retry-After without asking HttpClient to materialize an invalid header as a typed value.
    /// Provider input is untrusted: an invalid value means that only the contractual minimum applies.
    /// Decimal delta-seconds are parsed with saturation because the HTTP grammar has no 32-bit limit;
    /// a very large but syntactically valid value must hit the configured cap, not be mistaken for a
    /// malformed header (and, in the final 502/503 response, escape through the official parser).
    /// </summary>
    private static RetryAfterValue? ReadRetryAfter(HttpResponseMessage response)
    {
        try
        {
            if (!response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)) return null;
            foreach (string value in values)
                if (TryParseRetryAfter(value, out RetryAfterValue parsed)) return parsed;
        }
        catch (FormatException)
        {
            // A malformed provider header must not turn a 429/503 into an unhandled host fault.
        }
        return null;
    }

    private static bool TryParseRetryAfter(string? raw, out RetryAfterValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string text = raw.Trim();
        if (text.All(char.IsAsciiDigit))
        {
            long seconds = 0;
            long maximumSeconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond;
            foreach (char character in text)
            {
                int digit = character - '0';
                if (seconds > (maximumSeconds - digit) / 10)
                {
                    seconds = maximumSeconds;
                    break;
                }
                seconds = seconds * 10 + digit;
            }
            value = new RetryAfterValue(TimeSpan.FromSeconds(seconds), null);
            return true;
        }

        if (DateTimeOffset.TryParseExact(text, "r", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset date))
        {
            value = new RetryAfterValue(null, date);
            return true;
        }
        return false;
    }

    private static bool IsTerminalStatus(HttpResponseMessage? response) =>
        response?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound or HttpStatusCode.Gone;

    private static DateTimeOffset AddSaturating(DateTimeOffset now, TimeSpan delay) =>
        delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;

    private static DateTimeOffset RetryAt(RetryAfterValue? header, DateTimeOffset now, TimeSpan maximumCooldown)
    {
        DateTimeOffset minimum = AddSaturating(now, RetryDelay);
        DateTimeOffset maximum = AddSaturating(now, maximumCooldown);
        DateTimeOffset? requested = RequestedRetryAt(header, now);
        return requested is DateTimeOffset value && value > minimum
            ? value > maximum ? maximum : value
            : minimum;
    }

    private static DateTimeOffset CooldownUntil(RetryAfterValue? header, DateTimeOffset now, TimeSpan maximumCooldown)
    {
        DateTimeOffset minimum = AddSaturating(now, MinimumCooldown);
        DateTimeOffset maximum = AddSaturating(now, maximumCooldown);
        DateTimeOffset? requested = RequestedRetryAt(header, now);
        return requested is DateTimeOffset value && value > minimum
            ? value > maximum ? maximum : value
            : minimum;
    }

    private static DateTimeOffset? RequestedRetryAt(RetryAfterValue? header, DateTimeOffset now) =>
        header?.Delta is TimeSpan delta && delta >= TimeSpan.Zero
            ? AddSaturating(now, delta)
            : header?.Date is DateTimeOffset date && date > now ? date : null;

    private async Task DelayUntilAsync(DateTimeOffset due, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay = due - _clock.GetUtcNow();
            if (delay <= TimeSpan.Zero) return;
            await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static HostHttpFailure Failure(string code, string message, int? status = null, Exception? exception = null) =>
        new(new FailureDetail(code, message, status));

    public void Dispose()
    {
        foreach (SourceState state in _states.Values) state.SendGate.Dispose();
        if (_disposeTransport) _transport.Dispose();
    }

    internal sealed class CooldownReset : IDisposable
    {
        private readonly SourceState _state;
        private readonly long _resetVersion;
        private int _completed;

        internal CooldownReset(SourceState state, long resetVersion)
        {
            _state = state;
            _resetVersion = resetVersion;
        }

        /// <summary>Commits the durable-first reset and releases the source transport barrier.</summary>
        internal bool Commit() => Complete(clear: true);

        /// <summary>Releases the barrier without clearing a fence after a persistence failure.</summary>
        public void Dispose() => Complete(clear: false);

        private bool Complete(bool clear)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
            lock (_state.Gate)
            {
                // A late 429 from a request admitted before the barrier changed the version. It is
                // never removed, whether this transaction commits or aborts.
                if (clear && _state.CooldownVersion == _resetVersion) _state.CooldownUntil = default;
                _state.Resetting = false;
                _state.ResetCompleted!.TrySetResult();
            }
            _state.SendGate.Release();
            return true;
        }
    }

    private readonly record struct RetryAfterValue(TimeSpan? Delta, DateTimeOffset? Date);

    internal sealed class SourceState
    {
        internal readonly SemaphoreSlim SendGate = new(1, 1);
        internal readonly object Gate = new();
        internal DateTimeOffset NextStart;
        internal DateTimeOffset CooldownUntil;
        internal long CooldownVersion;
        internal bool Resetting;
        internal TaskCompletionSource? ResetCompleted;
    }

    private sealed class AttemptWindow : IDisposable
    {
        private readonly CancellationTokenSource _timeout = new();
        private readonly CancellationTokenSource _linked;
        private readonly ITimer _timer;
        internal bool Expired => _timeout.IsCancellationRequested;
        internal CancellationToken Token => _linked.Token;

        internal AttemptWindow(TimeProvider clock, CancellationToken caller, DateTimeOffset due)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token);
            TimeSpan delay = due - clock.GetUtcNow();
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            _timer = clock.CreateTimer(static state =>
            {
                try { ((CancellationTokenSource)state!).Cancel(); }
                catch (ObjectDisposedException) { }
            }, _timeout, delay, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _timer.Dispose();
            _linked.Dispose();
            _timeout.Dispose();
        }
    }

    private sealed class BooruBodyTooLargeException : Exception { }
    private sealed class BooruEncodingException(Exception? inner = null) : Exception("Unsupported booru content encoding.", inner) { }
    private sealed class BooruAttemptTimeoutException(Exception inner) : Exception("Booru response timed out.", inner) { }
}
