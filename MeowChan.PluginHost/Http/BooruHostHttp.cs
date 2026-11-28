using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Secrets;
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
    private readonly bool _disposeTransport;
    private readonly TimeProvider _clock;
    private readonly string _userAgent;
    private readonly string _familyUserAgent;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;
    private readonly Dictionary<string, BooruSourceProfile> _profiles;
    private readonly Func<TimeSpan>? _maximumCooldownProvider;
    private readonly SourceAdmission? _sourceAdmission;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, SourceState> _states = new(StringComparer.Ordinal);
    private readonly SourceState? _familyState;
    private int _stateUsers;
    private bool _disposed;

    // Keep the original public constructor shape for already compiled PluginHost consumers. The
    // configurable cap is an internal host wiring detail, not a new SDK surface.
    public BooruHostHttp(HttpClient transport, RequestPolicyState policy, IReadOnlyList<SourceDescription> sources,
        string? danbooruUserId = null, string? productVersion = null, bool disposeTransport = false)
        : this(transport, policy, sources, null, danbooruUserId, productVersion, disposeTransport)
    {
    }

    internal BooruHostHttp(HttpClient transport, RequestPolicyState policy, IReadOnlyList<SourceDescription> sources,
        Func<TimeSpan>? maximumCooldownProvider, string? danbooruUserId = null,
        string? productVersion = null, bool disposeTransport = false, SourceAdmission? sourceAdmission = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(policy);
        _clock = policy.TimeProvider;
        _disposeTransport = disposeTransport;
        _maximumCooldownProvider = maximumCooldownProvider;
        _sourceAdmission = sourceAdmission;
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
        if (_sources.Values.Any(BooruEndpointPolicy.IsE621Family))
        {
            lock (E621Family)
            {
                _familyState = E621Family.GetValue(policy, static _ => new SourceState());
                _familyState.AddOwner();
            }
        }
    }

    // The owner updates the generation under Gate. Reservations and provider fences
    // hold it while publishing changes so a site switch orders against both.
    internal sealed record SourceAdmission(object Gate, Func<string, SourceGeneration?> Current,
        CancellationToken Shutdown = default);
    internal readonly record struct SourceGeneration(long PluginRevision, long SiteRevision, Task? SiteGenerationEnded);

    internal IReadOnlyDictionary<string, SourceDescription> Sources => _sources;

    /// <summary>The enforced post-search profile of a declared source; null when it has none (fail closed).</summary>
    internal BooruSourceProfile? ProfileOf(string sourceId) =>
        sourceId is not null && _profiles.TryGetValue(sourceId, out BooruSourceProfile? profile) ? profile : null;
    internal TimeProvider TimeProvider => _clock;

    internal SourceGeneration? CaptureSourceGeneration(string sourceId)
    {
        if (_sourceAdmission is not { } admission) return null;
        lock (admission.Gate) return admission.Current(sourceId);
    }

    // Only a typed host operation can authorize account use for this send.
    internal async Task<HostHttpResult> SendAsync(HostHttpRequest request, SourceDescription source,
        BooruJob job, CancellationToken cancellationToken, BooruCallBinding? call = null)
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

        SourceGeneration? generation = call is null ? CaptureSourceGeneration(source.SourceId) : call.SourceGeneration;
        BooruAccountBinding.Operation? account = call?.Account;
        CancellationToken shutdown = _sourceAdmission?.Shutdown ?? default;
        if (!IsCurrent()) return StaleSource();

        using StateUse? use = TryUseState(source);
        if (use is null) return StaleSource();
        SourceState state = use.State;
        TimeSpan spacing = BooruEndpointPolicy.Spacing(ProfileOf(source.SourceId), MinimumStartSpacing);
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.Token, shutdown);
        CancellationToken token = abort.Token;
        Task? accountWatch = account is null ? null : CancelWhenGenerationEndsAsync(account.GenerationEnded, abort);
        Task? sourceWatch = generation?.SiteGenerationEnded is { } siteEnded ? CancelWhenGenerationEndsAsync(siteEnded, abort) : null;

        bool IsSourceCurrent() => !shutdown.IsCancellationRequested && (_sourceAdmission is null
            || generation is not null && CaptureSourceGeneration(source.SourceId) == generation);

        bool IsCurrent() => IsSourceCurrent() && account?.IsCurrent() != false;

        HostHttpFailure? ReserveSend(int? status)
        {
            lock (_sourceAdmission?.Gate ?? state.Gate)
            {
                if (!IsSourceCurrent()) return StaleSource();
                cancellationToken.ThrowIfCancellationRequested();
                if (account is null) return Reserve();

                HostHttpFailure? failure = null;
                // Keep the account generation current for both budget mutations.
                return account.TryPublish(() => failure = Reserve()) ? failure : StaleSource();
            }

            HostHttpFailure? Reserve()
            {
                if (!job.TryReserveSend())
                    return JobEnded(job, status) ?? Failure("booru-budget", "The booru job send budget is exhausted.", status);
                state.RecordStart(_clock.GetUtcNow(), spacing);
                return null;
            }
        }

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
                DateTimeOffset challengeUntil;
                lock (state.Gate)
                {
                    cooldownUntil = ClampCooldown(state, now);
                    challengeUntil = state.ChallengeUntil;
                }
                if (cooldownUntil > now)
                    return new HostHttpDeferred(cooldownUntil, "booru-cooldown", (int)HttpStatusCode.TooManyRequests);
                if (challengeUntil > now) return Challenge();
                if (now >= job.Deadline) return Failure("booru-budget", "The booru job budget is exhausted.");

                DateTimeOffset due = state.NextStartFor(spacing);
                if (due > now)
                {
                    DateTimeOffset waitUntil = due < job.Deadline ? due : job.Deadline;
                    await DelayUntilAsync(waitUntil, token).ConfigureAwait(false);
                    continue;
                }

                if (ReserveSend(null) is { } stop) return stop;
                string userAgent = BooruEndpointPolicy.IsE621Family(source) ? _familyUserAgent : _userAgent;
                HostHttpResult result = await SendWithRetryAsync(request, job, state, spacing, userAgent,
                    account, IsCurrent, ReserveSend, cancellationToken, token).ConfigureAwait(false);
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
            if (accountWatch is not null || sourceWatch is not null)
            {
                await abort.CancelAsync().ConfigureAwait(false);
                if (accountWatch is not null) await accountWatch.ConfigureAwait(false);
                if (sourceWatch is not null) await sourceWatch.ConfigureAwait(false);
            }
        }
    }

    private static async Task CancelWhenGenerationEndsAsync(Task generationEnded, CancellationTokenSource abort)
    {
        try { await generationEnded.WaitAsync(abort.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { return; }
        await abort.CancelAsync().ConfigureAwait(false);
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
        using StateUse? use = _sources.TryGetValue(sourceId, out SourceDescription? source) ? TryUseState(source) : null;
        if (use is not null)
        {
            SourceState state = use.State;
            DateTimeOffset now = _clock.GetUtcNow();
            lock (state.Gate)
            {
                DateTimeOffset cooldown = ClampCooldown(state, now);
                DateTimeOffset fence = cooldown > state.ChallengeUntil ? cooldown : state.ChallengeUntil;
                if (fence > now)
                {
                    retryAt = fence;
                    return true;
                }
            }
        }
        retryAt = default;
        return false;
    }

    // Callers hold SourceState.Gate while reading or updating the provider fences.
    private DateTimeOffset ClampCooldown(SourceState state, DateTimeOffset now)
    {
        DateTimeOffset cap = AddSaturating(now, GetMaximumCooldown());
        if (state.CooldownUntil > cap) state.CooldownUntil = cap;
        return state.CooldownUntil;
    }

    /// <summary>
    /// Starts a host-fence reset transaction. The source gate is held until the caller either commits
    /// after durable scheduler persistence or disposes to abort. This ordering is intentional: a request
    /// cannot start in the gap between the durable reset and transport-fence release.
    /// </summary>
    internal CooldownReset? BeginCooldownReset(string sourceId)
    {
        if (!_sources.TryGetValue(sourceId, out SourceDescription? source)) return null;
        StateUse? use = TryUseState(source);
        try
        {
            if (use is null) return null;
            CooldownReset reset = EnterCooldownReset(use);
            use = null; // The reset now owns the state until it releases the barrier.
            return reset;
        }
        finally { use?.Dispose(); }
    }

    private static CooldownReset EnterCooldownReset(StateUse use)
    {
        SourceState state = use.State;

        bool resetting = false;
        bool entered = false;
        try
        {
            long resetVersion;
            while (true)
            {
                Task completion;
                lock (state.Gate)
                {
                    if (!state.Resetting)
                    {
                        state.Resetting = true;
                        resetting = true;
                        state.ResetCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        // A new provider fence from an admitted request must survive this reset.
                        resetVersion = state.CooldownVersion;
                        break;
                    }
                    completion = state.ResetCompleted!.Task;
                }
                completion.GetAwaiter().GetResult();
            }

            // Resetting is already visible under Gate, so a waiter that acquires the
            // semaphore before this call observes the barrier and waits.
            state.SendGate.Wait();
            entered = true;
            return new CooldownReset(use, resetVersion);
        }
        catch
        {
            if (resetting)
            {
                lock (state.Gate)
                {
                    state.Resetting = false;
                    state.ResetCompleted!.TrySetResult();
                }
                if (entered) state.SendGate.Release();
            }
            throw;
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
        BooruJob job, SourceState state, TimeSpan spacing, string userAgent, BooruAccountBinding.Operation? account,
        Func<bool> isCurrent, Func<int?, HostHttpFailure?> reserveSend, CancellationToken cancellationToken, CancellationToken abort)
    {
        bool retried = false;
        while (true)
        {
            BooruAccountAttempt? attempt = null;
            HttpResponseMessage? response = null;
            using AttemptWindow window = new(_clock, abort,
                Min(AddSaturating(_clock.GetUtcNow(), ResponseTimeout), job.Deadline));
            try
            {
                if (!isCurrent()) return StaleSource();
                attempt = await SendAttemptAsync(request, userAgent, account, window.Token).ConfigureAwait(false);
                response = attempt.Response;
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
                    return ProjectResponse(request.Url, attempt, []);
                if (status is >= 300 and <= 399)
                    return Failure("http-redirect", "Metadata redirects are not followed.", status);
                if (status == 429)
                {
                    DateTimeOffset cooldown = CooldownUntil(ReadRetryAfter(response), _clock.GetUtcNow(), GetMaximumCooldown());
                    HostHttpDeferred deferred = new(cooldown, "booru-cooldown", status);
                    lock (state.Gate)
                    lock (_sourceAdmission?.Gate ?? state.Gate)
                    {
                        if (!isCurrent()) return StaleSource();
                        state.CooldownVersion++;
                        if (cooldown > state.CooldownUntil) state.CooldownUntil = cooldown;
                        job.RecordCooldownStop(deferred);
                    }
                    // Closing cancels the job and can run callbacks synchronously.
                    job.Close();
                    return deferred;
                }
                if (status is 502 or 503 && !retried)
                {
                    retried = true;
                    RetryAfterValue? retryAfter = ReadRetryAfter(response);
                    attempt.Dispose();
                    attempt = null;
                    response = null;
                    if (await DelayRetryAsync(job, state, spacing, reserveSend, abort, retryAfter, status).ConfigureAwait(false) is { } stop)
                        return stop;
                    continue;
                }

                if (status == 200 && IsHtml(response.Content.Headers.ContentType?.MediaType))
                    return StopForChallenge(state, isCurrent);
                byte[] body = await ReadBodyAsync(response.Content, window, abort).ConfigureAwait(false);
                window.Token.ThrowIfCancellationRequested();
                if (!isCurrent()) return StaleSource();
                if (status == 200 && LooksLikeHtml(body)) return StopForChallenge(state, isCurrent);
                return ProjectResponse(request.Url, attempt, body);
            }
            catch (BooruAccountChangedException)
            {
                return StaleSource();
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
                if (await DelayRetryAsync(job, state, spacing, reserveSend, abort).ConfigureAwait(false) is { } stop)
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
                if (await DelayRetryAsync(job, state, spacing, reserveSend, abort).ConfigureAwait(false) is { } stop)
                    return stop;
                continue;
            }
            catch (IOException exception)
            {
                return Failure("http-network", "The booru metadata response could not be read.", response is null ? null : (int)response.StatusCode, exception: exception);
            }
            finally
            {
                attempt?.Dispose();
            }
        }
    }

    /// <summary>Waits for the single transport retry; returns the stop failure when the job cannot send again.</summary>
    private async Task<HostHttpFailure?> DelayRetryAsync(BooruJob job, SourceState state, TimeSpan spacing,
        Func<int?, HostHttpFailure?> reserveSend, CancellationToken abort, RetryAfterValue? retryAfter = null, int? status = null)
    {
        DateTimeOffset retryAt = RetryAt(retryAfter, _clock.GetUtcNow(), GetMaximumCooldown());
        DateTimeOffset nextStart = state.NextStartFor(spacing);
        if (nextStart > retryAt) retryAt = nextStart;
        if (retryAt >= job.Deadline || job.IsExpired)
            return Failure("booru-budget", "The booru job budget is exhausted.", status);
        // A cancellation here propagates to SendAsync, which maps a job end to its failure.
        await DelayUntilAsync(retryAt, abort).ConfigureAwait(false);
        return reserveSend(status);
    }

    private HostHttpFailure StopForChallenge(SourceState state, Func<bool> isCurrent)
    {
        DateTimeOffset until = AddSaturating(_clock.GetUtcNow(), MinimumCooldown);
        lock (state.Gate)
        lock (_sourceAdmission?.Gate ?? state.Gate)
        {
            if (!isCurrent()) return StaleSource();
            state.CooldownVersion++;
            state.ChallengeUntil = until;
        }
        return Challenge();
    }

    private static HostHttpFailure Challenge() => Failure("http-challenge", "The provider returned an HTML challenge.", 200);

    internal static HostHttpFailure StaleSource() => Failure("source_unavailable", "The booru source or account changed.");

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

    private async Task<BooruAccountAttempt> SendAttemptAsync(HostHttpRequest request, string userAgent,
        BooruAccountBinding.Operation? account, CancellationToken token)
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
        if (account is not null)
            return await account.SendAttemptAsync(request, HttpHostPurpose.Metadata, message, Send, token).ConfigureAwait(false);
        return new BooruAccountAttempt(await Send(message, token).ConfigureAwait(false), null, null);

        Task<HttpResponseMessage> Send(HttpRequestMessage outgoing, CancellationToken cancellation) =>
            _transport.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, cancellation);
    }

    private HostHttpResult ProjectResponse(Uri url, BooruAccountAttempt attempt, byte[] body)
    {
        HttpResponseMessage response = attempt.Response;
        int status = (int)response.StatusCode;
        IReadOnlyDictionary<string, string> headers = ReadHeaders(response,
            status is 502 or 503 ? GetMaximumCooldown() : null, _clock.GetUtcNow());
        string? etag = response.Headers.ETag?.ToString();
        if (attempt.IsAuthenticated && (!attempt.IsBodySafe(body) || attempt.ContainsCredential(etag)
            || headers.Values.Any(attempt.ContainsCredential)))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(body);
            return Failure("http-network", "The authenticated booru metadata response could not be verified.", status);
        }
        return new HostHttpResponse(status, url, headers, body, new SourceValidators(etag, response.Content.Headers.LastModified));
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

    private StateUse? TryUseState(SourceDescription source)
    {
        lock (_stateGate)
        {
            if (_disposed) return null;
            SourceState? state = BooruEndpointPolicy.IsE621Family(source) ? _familyState : _states.GetValueOrDefault(source.SourceId);
            if (state is null) _states.Add(source.SourceId, state = new SourceState());
            _stateUsers++;
            return new StateUse(this, state);
        }
    }

    private void ReleaseState()
    {
        lock (_stateGate)
            if (--_stateUsers == 0 && _disposed) DisposeStates();
    }

    // Called under _stateGate once the host is closed and all sends and resets released their gates.
    private void DisposeStates()
    {
        foreach (SourceState state in _states.Values) state.Dispose();
        _states.Clear();
        if (_familyState is null) return;
        lock (E621Family)
            _familyState.ReleaseOwner();
    }

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
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_stateUsers == 0) DisposeStates();
        }
        if (_disposeTransport) _transport.Dispose();
    }

    internal sealed class StateUse(BooruHostHttp owner, SourceState state) : IDisposable
    {
        internal SourceState State { get; } = state;

        public void Dispose() => owner.ReleaseState();
    }

    internal sealed class CooldownReset : IDisposable
    {
        private readonly StateUse _use;
        private readonly long _resetVersion;
        private int _completed;

        internal CooldownReset(StateUse use, long resetVersion)
        {
            _use = use;
            _resetVersion = resetVersion;
        }

        /// <summary>Commits the durable-first reset and releases the source transport barrier.</summary>
        internal bool Commit() => Complete(clear: true);

        /// <summary>Releases the barrier without clearing a fence after a persistence failure.</summary>
        public void Dispose() => Complete(clear: false);

        private bool Complete(bool clear)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
            SourceState state = _use.State;
            bool cleared;
            lock (state.Gate)
            {
                // Requests admitted before the barrier can create a new fence while reset waits.
                cleared = state.CooldownVersion == _resetVersion;
                if (clear && cleared)
                {
                    state.CooldownUntil = default;
                    state.ChallengeUntil = default;
                }
                state.Resetting = false;
                state.ResetCompleted!.TrySetResult();
            }
            state.SendGate.Release();
            _use.Dispose();
            return !clear || cleared;
        }
    }

    private readonly record struct RetryAfterValue(TimeSpan? Delta, DateTimeOffset? Date);

    internal sealed class SourceState : IDisposable
    {
        private SemaphoreSlim? _sendGate = new(1, 1);
        private int _owners; // Shared family hosts, guarded by E621Family.
        internal SemaphoreSlim SendGate => _sendGate ?? throw new ObjectDisposedException(nameof(SourceState));
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

        internal void AddOwner()
        {
            _sendGate ??= new(1, 1);
            _owners++;
        }

        internal void ReleaseOwner()
        {
            // The policy still owns the pacing and provider fences after the last host leaves.
            if (--_owners == 0) Dispose();
        }

        public void Dispose()
        {
            _sendGate?.Dispose();
            _sendGate = null;
        }
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
