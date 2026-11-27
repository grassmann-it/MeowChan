using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
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
    /// <summary>Host ceiling for numbered pages of every source; a source profile can only lower it.</summary>
    public const int MaximumSearchPage = MeowChan.Booru.DanbooruTagQuery.MaximumSearchPages;
    /// <summary>Host ceiling for the page size of every source; a source profile can only lower it.</summary>
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
    // The request policy belongs to the host session. Compatibility entry points and
    // separately activated plugin builds must share the family budget within it.
    private static readonly ConditionalWeakTable<RequestPolicyState, SourceState> E621Family = new();
    private readonly HttpClient _transport;
    private readonly RequestPolicyState _policy;
    private readonly bool _disposeTransport;
    private readonly TimeProvider _clock;
    private readonly string _userAgent;
    private readonly string _familyUserAgent;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;
    private readonly Dictionary<string, BooruSourceProfile> _profiles;
    private readonly Func<TimeSpan>? _maximumCooldownProvider;
    private readonly Func<long?>? _sourceRevision;
    private readonly Func<SourceDescription, Uri, object, BooruMetadataAccountLease?>? _metadataAccount;
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
        string? productVersion = null, bool disposeTransport = false, Func<long?>? sourceRevision = null,
        Func<SourceDescription, Uri, object, BooruMetadataAccountLease?>? metadataAccount = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _clock = policy.TimeProvider;
        _disposeTransport = disposeTransport;
        _maximumCooldownProvider = maximumCooldownProvider;
        _sourceRevision = sourceRevision;
        _metadataAccount = metadataAccount;
        _sources = (sources ?? throw new ArgumentNullException(nameof(sources)))
            .Where(source => source is not null)
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        // Resolved once: a post-search source sends only through the profile the host validated at composition.
        Dictionary<string, BooruSourceProfile> profiles = new(StringComparer.Ordinal);
        foreach (SourceDescription source in _sources.Values)
            if (BooruEndpointPolicy.Resolve(source) is BooruSourceProfile profile) profiles[source.SourceId] = profile;
        _profiles = profiles;
        string version = productVersion ?? BooruUserAgent.ProductVersion();
        _userAgent = BooruUserAgent.Build(version, danbooruUserId);
        _familyUserAgent = BooruUserAgent.BuildForE621Family(version);
    }

    internal IReadOnlyDictionary<string, SourceDescription> Sources => _sources;

    /// <summary>The enforced post-search profile of a declared source; null when it has none (fail closed).</summary>
    internal BooruSourceProfile? ProfileOf(string sourceId) =>
        sourceId is not null && _profiles.TryGetValue(sourceId, out BooruSourceProfile? profile) ? profile : null;
    internal TimeProvider TimeProvider => _clock;

    internal Task<HostHttpResult> SendAsync(HostHttpRequest request, SourceDescription source,
        BooruJob job, CancellationToken cancellationToken) => SendCoreAsync(request, source, job, null, cancellationToken);

    // Only a typed host operation can opt into account access. The offered transport must
    // disable redirects, cookies and default credentials (PluginHostHttp.CreateTransport).
    internal Task<HostHttpResult> SendBoundAsync(HostHttpRequest request, SourceDescription source,
        object operation, BooruJob job, CancellationToken cancellationToken) =>
        BooruEndpointPolicy.Bind(request, source, operation) is { } bound
            ? SendCoreAsync(bound, source, job, operation, cancellationToken)
            : Task.FromResult<HostHttpResult>(BooruEndpointPolicy.OperationDenied());

    private async Task<HostHttpResult> SendCoreAsync(HostHttpRequest request, SourceDescription source,
        BooruJob job, object? operation, CancellationToken cancellationToken)
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
        if (!BooruEndpointPolicy.IsAllowed(request.Url, source, ProfileOf(source.SourceId)))
            return Failure("http-request", "The booru endpoint is not on the provider allowlist.");
        if (HasForbiddenTransportDefaults() || HasForbiddenRequestHeaders(request.Headers))
            return Failure("http-request", "The booru transport contains host-owned credentials.");
        if (job.IsExpired) return Failure("booru-budget", "The booru job budget is exhausted.");

        long? revision = _sourceRevision?.Invoke();
        if (_sourceRevision is not null && revision is null) return StaleSource();
        BooruMetadataAccountLease? account = operation is null ? null : _metadataAccount?.Invoke(source, request.Url, operation);
        if (account is not null && !BooruEndpointPolicy.IsExactOrigin(account.Origin, request.Url))
            return BooruEndpointPolicy.OperationDenied();
        CancellationToken accountLifetime = account?.Lifetime ?? default;
        bool IsCurrent() => !accountLifetime.IsCancellationRequested && (_sourceRevision is null || _sourceRevision() == revision);

        SourceState state = StateFor(source);
        TimeSpan spacing = BooruEndpointPolicy.Spacing(ProfileOf(source.SourceId), MinimumStartSpacing);
        // Every wait and attempt observes both the caller and the job lifetime. Closing the
        // job (or reaching its deadline) therefore aborts a queued send-gate admission,
        // provider spacing, retry delays, the header wait and body reads alike.
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.Token, accountLifetime);
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
                if (!IsCurrent()) return StaleSource();
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
                    return new HostHttpDeferred(cooldownUntil, "booru-cooldown", (int)HttpStatusCode.TooManyRequests);
                if (state.ChallengeUntil > now) return Challenge();
                if (now >= job.Deadline) return Failure("booru-budget", "The booru job budget is exhausted.");

                DateTimeOffset due = state.NextStartFor(spacing);
                if (due > now)
                {
                    DateTimeOffset waitUntil = due < job.Deadline ? due : job.Deadline;
                    await DelayUntilAsync(waitUntil, token).ConfigureAwait(false);
                    continue;
                }

                if (!job.TryReserveSend())
                    return JobEnded(job) ?? Failure("booru-budget", "The booru job send budget is exhausted.");
                state.RecordStart(_clock.GetUtcNow(), spacing);
                string userAgent = BooruEndpointPolicy.IsE621Family(source) ? _familyUserAgent : _userAgent;
                HostHttpResult result = await SendWithRetryAsync(request, job, state, spacing, userAgent,
                    account, IsCurrent, cancellationToken, token).ConfigureAwait(false);
                return IsCurrent() ? result : StaleSource();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !IsCurrent())
        {
            return StaleSource();
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
        if (_sources.TryGetValue(sourceId, out SourceDescription? source))
        {
            SourceState state = StateFor(source);
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
        if (!_sources.TryGetValue(sourceId, out SourceDescription? source)) return null;
        SourceState state = StateFor(source);

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
        using CooldownReset? reset = BeginCooldownReset(sourceId);
        return reset?.Commit() ?? false;
    }

    internal TimeSpan GetMaximumCooldown()
    {
        TimeSpan value;
        try { value = _maximumCooldownProvider?.Invoke() ?? DefaultMaximumCooldown; }
        catch { return DefaultMaximumCooldown; }
        return value < MinimumConfiguredCooldown ? MinimumConfiguredCooldown
            : value > MaximumConfiguredCooldown ? MaximumConfiguredCooldown : value;
    }

    private async Task<HostHttpResult> SendWithRetryAsync(HostHttpRequest request,
        BooruJob job, SourceState state, TimeSpan spacing, string userAgent, BooruMetadataAccountLease? account,
        Func<bool> isCurrent, CancellationToken cancellationToken, CancellationToken abort)
    {
        bool retried = false;
        while (true)
        {
            HttpResponseMessage? response = null;
            using AttemptWindow window = new(_clock, abort,
                Min(AddSaturating(_clock.GetUtcNow(), ResponseTimeout), job.Deadline));
            try
            {
                if (!isCurrent()) return StaleSource();
                AuthenticationHeaderValue? authorization = null;
                if (account?.ReadAuthorization is { } readAuthorization)
                {
                    authorization = await readAuthorization(window.Token).ConfigureAwait(false);
                    if (authorization is null)
                        return Failure("booru-account-unavailable", "The booru account is not available.");
                }
                if (!isCurrent()) return StaleSource();
                response = await SendAttemptAsync(request, userAgent, authorization, window.Token).ConfigureAwait(false);
                // A late transport completion must observe the response deadline as well as
                // job/caller cancellation before any status handling, retry or body read.
                window.Token.ThrowIfCancellationRequested();
                if (!isCurrent()) return StaleSource();
                if (response.RequestMessage?.RequestUri is Uri actual && actual != request.Url)
                    return Failure("http-redirect", "Metadata redirects are not followed.");

                int status = (int)response.StatusCode;
                // Decide on redirects, cooldowns and retries before any body byte is read.
                // A 304 answers the plugin's own conditional request: it is not a redirect and
                // carries no body. An unconditional 304 stays a terminal 3xx like any other.
                if (status == 304 && PluginRequestHeaders.IsConditional(request))
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
                    RetryAfterValue? retryAfter = ReadRetryAfter(response);
                    response.Dispose();
                    response = null;
                    if (await DelayRetryAsync(job, state, spacing, abort, retryAfter, status).ConfigureAwait(false) is { } stop)
                        return stop;
                    continue;
                }

                if (status == 200 && IsHtml(response.Content.Headers.ContentType?.MediaType))
                    return StopForChallenge(state);
                byte[] body = await ReadBodyAsync(response.Content, window, abort).ConfigureAwait(false);
                window.Token.ThrowIfCancellationRequested();
                if (!isCurrent()) return StaleSource();
                if (status == 200 && LooksLikeHtml(body)) return StopForChallenge(state);
                return new HostHttpResponse(status, request.Url,
                    ReadHeaders(response, status is 502 or 503 ? GetMaximumCooldown() : null, _clock.GetUtcNow()), body,
                    new SourceValidators(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested && !isCurrent())
            {
                return StaleSource();
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
                if (await DelayRetryAsync(job, state, spacing, abort).ConfigureAwait(false) is { } stop)
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
                if (await DelayRetryAsync(job, state, spacing, abort).ConfigureAwait(false) is { } stop)
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
    private async Task<HostHttpFailure?> DelayRetryAsync(BooruJob job, SourceState state, TimeSpan spacing,
        CancellationToken abort, RetryAfterValue? retryAfter = null, int? status = null)
    {
        DateTimeOffset retryAt = RetryAt(retryAfter, _clock.GetUtcNow(), GetMaximumCooldown());
        DateTimeOffset nextStart = state.NextStartFor(spacing);
        if (nextStart > retryAt) retryAt = nextStart;
        if (retryAt >= job.Deadline || job.IsExpired)
            return Failure("booru-budget", "The booru job budget is exhausted.", status);
        // A cancellation here propagates to SendAsync, which maps a job end to its failure.
        await DelayUntilAsync(retryAt, abort).ConfigureAwait(false);
        if (JobEnded(job, status) is HostHttpFailure ended) return ended;
        if (!job.TryReserveSend())
            return JobEnded(job, status) ?? Failure("booru-budget", "The booru job send budget is exhausted.", status);
        state.RecordStart(_clock.GetUtcNow(), spacing);
        return null;
    }

    private HostHttpFailure StopForChallenge(SourceState state)
    {
        state.ChallengeUntil = AddSaturating(_clock.GetUtcNow(), MinimumCooldown);
        return Challenge();
    }

    private static HostHttpFailure Challenge() => Failure("http-challenge", "The provider returned an HTML challenge.", 200);

    private static HostHttpFailure StaleSource() => Failure("source_unavailable", "The booru source or account changed.");

    private static bool IsHtml(string? mediaType) =>
        string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHtml(ReadOnlySpan<byte> body)
    {
        if (body.StartsWith("\uFEFF"u8)) body = body[3..];
        int offset = body.IndexOfAnyExcept(" \r\n\t"u8);
        if (offset < 0 || body[offset] != (byte)'<') return false;
        ReadOnlySpan<char> prefix = Encoding.ASCII.GetString(body.Slice(offset, Math.Min(16, body.Length - offset)));
        return prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(HostHttpRequest request, string userAgent,
        AuthenticationHeaderValue? authorization, CancellationToken token)
    {
        // The send was reserved under the job gate; a Close racing that reservation has
        // already cancelled this token, so the transport is never entered afterwards.
        token.ThrowIfCancellationRequested();
        using HttpRequestMessage message = new(HttpMethod.Get, request.Url);
        message.Headers.UserAgent.Clear();
        message.Headers.UserAgent.ParseAdd(userAgent);
        message.Headers.AcceptEncoding.Clear();
        message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        message.Options.Set(MeteringHandler.SourceKey, request.SourceId);
        PluginRequestHeaders.Apply(message, request);
        message.Headers.Authorization = authorization;
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

    private SourceState StateFor(SourceDescription source) => BooruEndpointPolicy.IsE621Family(source)
        ? E621Family.GetValue(_policy, static _ => new SourceState())
        : _states.GetOrAdd(source.SourceId, static _ => new SourceState());

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
        foreach (SourceState state in _states.Values) state.Dispose();
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
            bool cleared = false;
            lock (_state.Gate)
            {
                // A late 429 from a request admitted before the barrier changed the version. It is
                // never removed, whether this transaction commits or aborts. Report that the commit
                // did not clear the fence, so the caller cannot expose a false reset success.
                cleared = _state.CooldownVersion == _resetVersion;
                if (clear && cleared) _state.CooldownUntil = default;
                _state.Resetting = false;
                _state.ResetCompleted!.TrySetResult();
            }
            _state.SendGate.Release();
            return !clear || cleared;
        }
    }

    private readonly record struct RetryAfterValue(TimeSpan? Delta, DateTimeOffset? Date);

    internal sealed class SourceState : IDisposable
    {
        internal readonly SemaphoreSlim SendGate = new(1, 1);
        internal readonly object Gate = new();
        private DateTimeOffset _lastStart;
        private TimeSpan _lastSpacing;
        internal DateTimeOffset ChallengeUntil;
        internal DateTimeOffset CooldownUntil;
        internal long CooldownVersion;
        internal bool Resetting;
        internal TaskCompletionSource? ResetCompleted;

        // Read and written only while holding SendGate. Use the stricter of the preceding
        // and the current declaration when alternating hosts in the same budget.
        internal DateTimeOffset NextStartFor(TimeSpan spacing) => _lastStart == default ? default
            : AddSaturating(_lastStart, spacing > _lastSpacing ? spacing : _lastSpacing);

        internal void RecordStart(DateTimeOffset started, TimeSpan spacing)
        {
            _lastStart = started;
            _lastSpacing = spacing;
        }

        public void Dispose() => SendGate.Dispose();
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

/// <summary>
/// Host-owned account grant scoped to an exact metadata origin and one profile/account generation.
/// A null reader explicitly permits anonymous metadata and still fences the inactive account generation.
/// </summary>
internal sealed class BooruMetadataAccountLease(Uri origin, CancellationToken lifetime,
    Func<CancellationToken, ValueTask<AuthenticationHeaderValue?>>? readAuthorization)
{
    internal Uri Origin { get; } = origin;
    internal CancellationToken Lifetime { get; } = lifetime;
    internal Func<CancellationToken, ValueTask<AuthenticationHeaderValue?>>? ReadAuthorization { get; } = readAuthorization;
}
