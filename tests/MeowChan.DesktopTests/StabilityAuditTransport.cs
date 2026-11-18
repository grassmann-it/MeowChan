using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MeowChan.DesktopTests;

internal enum StabilityStep { BoardList, Catalog, Thread, Original }

/// <summary>
/// One audited request. <see cref="Sent"/> is true only when a request really reached the network client;
/// refusals and plan answers never did. The URL carries no query or credentials.
/// </summary>
internal sealed record StabilityRequestRecord(int Sequence, string? Board, string Step, string Url, int? Status, long Bytes,
    string? Sha256, string? ContentType, string Outcome, bool Sent, DateTimeOffset AtUtc,
    IReadOnlyDictionary<string, string>? ResponseHeaders = null, string? UserAgent = null);

/// <summary>A response or condition that stopped the chan for this run.</summary>
internal sealed record StabilityFailure(string? Board, string Step, string Mode, int? Status, string? Url, string? Detail);

/// <summary>The received body and, for an original, the Content-Length the response declared (null when it declared none).</summary>
internal sealed record StabilityDigests(long Length, string Sha256, string Md5Hex, string Sha1Hex, long? ContentLength = null);

/// <summary>What the transport may let through for one chan: its declared hosts and the user's planned redirects.</summary>
internal sealed record StabilityChanPolicy(string SourceId, IReadOnlyCollection<string> AllowedHosts,
    IReadOnlyList<StabilityRedirect> PlannedRedirects);

/// <summary>
/// The only handler that can reach the real transport during a stability run. It enforces the contract budget
/// per board: one catalog GET, one thread GET (at most 8 MiB each) and one original (at most 2 MiB), plus the
/// one board-list GET and the host pairs of planned redirects. A step is armed with its exact URL by the harness;
/// that URL comes from the plugin's own code (computed without network) or from a previous step's result.
/// Everything else is refused before the socket and recorded. The first unexpected response stops the chan.
/// With a null network client the handler cannot send anything at all (dry run, offline phase).
/// </summary>
internal sealed class StabilityAuditTransport : HttpMessageHandler
{
    internal const int MaximumOriginalBytes = 2 * 1024 * 1024;
    internal const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ParkLimit = TimeSpan.FromSeconds(60);
    // Strong signals only. Ordinary imageboard pages carry a captcha post form and a noscript notice, so words such as
    // "captcha", "enable javascript" or "are you a robot" are not evidence of a challenge.
    private static readonly string[] ChallengeMarkers =
    [
        "cf-browser-verification", "challenge-platform", "cf-chl", "ddos-guard", "powblock", "proof-of-work", "proof of work"
    ];
    private static readonly System.Text.RegularExpressions.Regex ChallengeTitle = new(
        @"<title[^>]*>\s*(just a moment|attention required)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private sealed class Armed(StabilityStep step, string? board, string url)
    {
        internal StabilityStep Step { get; } = step;
        internal string? Board { get; } = board;
        internal string Url { get; } = url;
        internal bool FirstSent, HopSent, Closed;
        internal string? PendingRedirect;
    }

    private enum DecisionKind { Allow, Refuse, Park, Plan }
    private readonly record struct Decision(DecisionKind Kind, string? Reason = null, Armed? Armed = null);

    private StabilityChanPolicy _policy;
    private readonly HttpClient? _network;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<StabilityRequestRecord> _rows = [];
    private readonly List<StabilityFailure> _failures = [];
    private readonly List<(Uri Uri, HttpRequestMessage Request, TaskCompletionSource<Decision> Source)> _parked = [];
    private readonly Dictionary<string, StabilityDigests> _digests = new(StringComparer.Ordinal);
    private readonly List<string> _planUrls = [];
    private Armed? _armed;
    private string? _awaitOriginalBoard;
    private string? _stop;
    private bool _planning;
    private int _sequence;
    private int _sends;

    internal StabilityAuditTransport(StabilityChanPolicy policy, HttpClient? network = null)
    {
        _policy = policy;
        _network = network;
    }

    /// <summary>The allowed hosts are only known once the delivered description is loaded. Until then nothing passes.</summary>
    internal void UpdatePolicy(StabilityChanPolicy policy)
    {
        lock (_gate) _policy = policy;
    }

    /// <summary>The offline phase: every request is refused and there is no network client to fall back to.</summary>
    internal bool RejectAll { get; init; }
    /// <summary>
    /// Where metadata response bodies (board list, catalog, thread; never media) are saved as &lt;sha256&gt;.body, outside Git.
    /// A failing response (challenge page, 429) is saved too, so a run can be replayed without network.
    /// </summary>
    internal string? BodyDirectory { get; init; }
    private readonly Dictionary<(string?, StabilityStep), byte[]> _bodies = new();
    internal int ActualNetworkSends => Volatile.Read(ref _sends);
    internal string? StopMode { get { lock (_gate) return _stop; } }
    internal IReadOnlyList<StabilityRequestRecord> Rows { get { lock (_gate) return _rows.ToArray(); } }
    internal IReadOnlyList<StabilityFailure> Failures { get { lock (_gate) return _failures.ToArray(); } }
    internal StabilityDigests? OriginalDigests(string board) { lock (_gate) return _digests.GetValueOrDefault(board); }
    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Runs <paramref name="call"/> while every request is only recorded and answered with an empty JSON array.
    /// No network client is touched. The recorded URLs are what the plugin's own code would request.
    /// </summary>
    internal async Task<IReadOnlyList<string>> PlanAsync(Func<Task> call)
    {
        lock (_gate)
        {
            if (_planning) throw new InvalidOperationException("A plan is already running.");
            _planning = true;
            _planUrls.Clear();
        }
        try { await call().ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { /* the plan answer is not a real payload */ }
        finally { lock (_gate) _planning = false; }
        lock (_gate) return _planUrls.ToArray();
    }

    /// <summary>Allows exactly one GET of <paramref name="url"/> (plus one planned redirect hop) for this step.</summary>
    internal void Arm(StabilityStep step, string? board, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || !uri.IsDefaultPort
            || !_policy.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new LiveHarnessLimitException("armed-url-not-allowed");
        List<(Uri Uri, HttpRequestMessage Request, TaskCompletionSource<Decision> Source)> pending;
        lock (_gate)
        {
            if (_stop != null) throw new LiveHarnessLimitException("chan-stopped:" + _stop);
            Armed armed = new(step, board, uri.AbsoluteUri);
            _armed = armed;
            _awaitOriginalBoard = null;
            pending = _parked.ToList();
            _parked.Clear();
            foreach (var parked in pending)
                parked.Source.TrySetResult(step == StabilityStep.Original ? Decide(parked.Uri, parked.Request, allowPark: false)
                    : new(DecisionKind.Refuse, "unplanned-url"));
        }
    }

    /// <summary>No original can be armed: parked media requests are refused and the chan accepts nothing else.</summary>
    internal void CloseSteps()
    {
        lock (_gate)
        {
            _armed = null;
            _awaitOriginalBoard = null;
            foreach (var parked in _parked) parked.Source.TrySetResult(new(DecisionKind.Refuse, "no-eligible-original"));
            _parked.Clear();
        }
    }

    internal void CancelPending() => _lifetime.Cancel();

    private Decision Decide(Uri uri, HttpRequestMessage request, bool allowPark = true)
    {
        // Callers hold _gate.
        if (RejectAll) return new(DecisionKind.Refuse, "offline-request");
        if (_planning) { _planUrls.Add(AuditUrl(uri.AbsoluteUri)); return new(DecisionKind.Plan); }
        if (_stop != null) return new(DecisionKind.Refuse, "chan-stopped:" + _stop);
        if (request.Method != HttpMethod.Get || request.Headers.Range != null) return new(DecisionKind.Refuse, "method-or-range");
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort
            || !_policy.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            return new(DecisionKind.Refuse, "host-not-allowed");
        Armed? armed = _armed;
        string absolute = uri.AbsoluteUri;
        if (armed == null || armed.Closed)
        {
            if (allowPark && _awaitOriginalBoard != null) return new(DecisionKind.Park);
            return new(DecisionKind.Refuse, "no-step-armed");
        }
        if (!armed.FirstSent && absolute == armed.Url) { armed.FirstSent = true; return new(DecisionKind.Allow, Armed: armed); }
        if (armed.PendingRedirect != null && !armed.HopSent && absolute == armed.PendingRedirect)
        {
            armed.HopSent = true;
            armed.PendingRedirect = null;
            return new(DecisionKind.Allow, Armed: armed);
        }
        if (armed.FirstSent && absolute == armed.Url) return new(DecisionKind.Refuse, "duplicate-request");
        return new(DecisionKind.Refuse, armed.Step == StabilityStep.Original ? "extra-media" : "unplanned-url");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Uri uri = request.RequestUri ?? throw new LiveHarnessLimitException("missing-uri");
        string url = AuditUrl(uri.AbsoluteUri);
        Decision decision;
        TaskCompletionSource<Decision>? source = null;
        lock (_gate)
        {
            decision = Decide(uri, request);
            if (decision.Kind == DecisionKind.Park)
            {
                source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _parked.Add((uri, request, source));
            }
        }
        if (source != null)
        {
            // Media requests that arrive before the harness has chosen the original wait here; they never reach the socket
            // unless they turn out to be that exact original.
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            wait.CancelAfter(ParkLimit);
            try { decision = await source.Task.WaitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                lock (_gate) _parked.RemoveAll(row => ReferenceEquals(row.Source, source));
                token.ThrowIfCancellationRequested();
                decision = new(DecisionKind.Refuse, "park-timeout");
            }
        }
        if (decision.Kind == DecisionKind.Plan)
        {
            lock (_gate) Record(null, "plan", url, 200, 2, null, "application/json", "plan-answer", sent: false);
            HttpResponseMessage plan = new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Encoding.ASCII.GetBytes("[]")) };
            plan.Content.Headers.ContentType = new("application/json");
            return plan;
        }
        if (decision.Kind != DecisionKind.Allow)
        {
            lock (_gate) Record(_armed?.Board, _armed?.Step.ToString().ToLowerInvariant() ?? "none", url, null, 0, null, null, "refused:" + decision.Reason, sent: false);
            throw new LiveHarnessLimitException(decision.Reason ?? "refused");
        }

        Armed armed = decision.Armed!;
        int maximum = armed.Step == StabilityStep.Original ? MaximumOriginalBytes : MaximumMetadataBytes;
        string step = armed.Step.ToString().ToLowerInvariant();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using HttpRequestMessage outbound = new(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) outbound.Headers.TryAddWithoutValidation(header.Key, header.Value);
        HttpResponseMessage? response = null;
        int? status = null;
        string? contentType = null;
        IReadOnlyDictionary<string, string>? headers = null;
        string? agent = null;
        long received = 0;
        try
        {
            if (_network == null) throw new LiveHarnessLimitException("network-unavailable");
            agent = StaticAgent(outbound.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : _network.DefaultRequestHeaders.UserAgent.ToString());
            Interlocked.Increment(ref _sends);
            response = await _network.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            contentType = response.Content.Headers.ContentType?.ToString();
            long? declaredLength = response.Content.Headers.ContentLength;
            headers = SafeResponseHeaders(response);
            agent = StaticAgent(response.RequestMessage?.Headers.UserAgent.ToString() ?? "");
            if (response.RequestMessage?.RequestUri != outbound.RequestUri) throw new LiveHarnessLimitException("unexpected-final-uri");
            byte[] bytes = await LiveSourceAuditTransport.ReadBoundedAsync(response.Content, maximum, deadline.Token, count => received += count).ConfigureAwait(false);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            (string outcome, string? failureMode, string? detail, string? redirect) = Classify(armed, uri, request, response, status.Value, contentType, bytes);
            if (armed.Step != StabilityStep.Original) KeepBody(armed, bytes, hash);
            ByteArrayContent buffered = new(bytes);
            foreach (var header in response.Content.Headers) buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
            HttpContent old = response.Content;
            response.Content = buffered;
            old.Dispose();
            lock (_gate)
            {
                Record(armed.Board, step, url, status, bytes.Length, hash, contentType, outcome, sent: true, headers, agent);
                if (failureMode != null)
                {
                    Stop(armed, failureMode, status, url, detail);
                    armed.Closed = true;
                }
                else if (redirect != null) armed.PendingRedirect = redirect;
                else
                {
                    armed.Closed = true;
                    if (armed.Step == StabilityStep.Thread) _awaitOriginalBoard = armed.Board;
                    if (armed.Step == StabilityStep.Original && armed.Board != null)
                        _digests[armed.Board] = new(bytes.Length, hash, Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),
                            Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), declaredLength);
                }
            }
            return response;
        }
        catch (Exception error)
        {
            response?.Dispose();
            string mode = error is LiveHarnessLimitException limit ? (limit.Code is "declared-body-too-large" or "body-reached-limit-without-eof" ? "size-cap:" + limit.Code : limit.Code)
                : error is OperationCanceledException ? "timeout" : "transport-error:" + error.GetType().Name;
            lock (_gate)
            {
                Record(armed.Board, step, url, status, received, null, contentType, "failure:" + mode, sent: _network != null, headers, agent);
                Stop(armed, mode, status, url, null);
                armed.Closed = true;
            }
            throw;
        }
    }

    private void KeepBody(Armed armed, byte[] bytes, string hash)
    {
        lock (_gate) _bodies[(armed.Board, armed.Step)] = bytes;
        if (BodyDirectory == null || bytes.Length == 0) return;
        try
        {
            Directory.CreateDirectory(BodyDirectory);
            string path = Path.Combine(BodyDirectory, hash + ".body");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        }
        catch (Exception) { /* a missing diagnostic copy must not change what the run observed */ }
    }

    /// <summary>The last metadata body of a step, for the harness's own reading of sticky flags and age.</summary>
    internal byte[]? Body(string? board, StabilityStep step) { lock (_gate) return _bodies.GetValueOrDefault((board, step)); }

    private (string Outcome, string? FailureMode, string? Detail, string? Redirect) Classify(Armed armed, Uri requestUri,
        HttpRequestMessage request, HttpResponseMessage response, int status, string? contentType, byte[] body)
    {
        if (status is 301 or 302 or 303 or 307 or 308)
        {
            Uri? target = response.Headers.Location is { } location ? (location.IsAbsoluteUri ? location : new Uri(requestUri, location)) : null;
            lock (_gate)
            {
                if (target != null && !armed.HopSent && target.UserInfo.Length == 0 && target.Fragment.Length == 0 && target.IsDefaultPort
                    && target.Scheme is "http" or "https"
                    && _policy.PlannedRedirects.Any(pair => string.Equals(pair.From, requestUri.IdnHost, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(pair.To, target.IdnHost, StringComparison.OrdinalIgnoreCase)))
                    return ("redirect-planned", null, null, target.AbsoluteUri);
            }
            return ("failure:unplanned-redirect", "unplanned-redirect", target == null ? "no Location" : AuditUrl(target.AbsoluteUri), null);
        }
        if (status != 200)
        {
            string? retry = response.Headers.RetryAfter?.ToString();
            return ("failure:http-" + status, "http-" + status, retry == null ? null : "retry-after=" + retry, null);
        }
        if (body.Length == 0) return ("failure:empty-body", "empty-body", null, null);
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        char first = FirstCharacter(body);
        if (armed.Step == StabilityStep.Original)
        {
            bool mediaTypeOk = mediaType != null && (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase));
            if (first == '<' || !mediaTypeOk)
                return LooksLikeChallenge(body) ? ("failure:challenge-page", "challenge-page", mediaType, null)
                    : ("failure:wrong-content-type", "wrong-content-type", mediaType ?? "none", null);
            return ("http-200", null, null, null);
        }
        bool wantsJson = request.Headers.Accept.Any(value => value.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            && armed.Step != StabilityStep.BoardList;
        bool jsonType = mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        if (first == '<')
        {
            if (LooksLikeChallenge(body)) return ("failure:challenge-page", "challenge-page", mediaType, null);
            if (wantsJson) return ("failure:wrong-content-type", "wrong-content-type", mediaType ?? "none", null);
        }
        else if (wantsJson && !jsonType) return ("failure:wrong-content-type", "wrong-content-type", mediaType ?? "none", null);
        return ("http-200", null, null, null);
    }

    private void Stop(Armed armed, string mode, int? status, string url, string? detail)
    {
        // Callers hold _gate. The first unexpected response ends the chan for this run; later failures keep the original cause.
        _stop ??= mode;
        _failures.Add(new(armed.Board, armed.Step.ToString().ToLowerInvariant(), mode, status, url, detail));
        foreach (var parked in _parked) parked.Source.TrySetResult(new(DecisionKind.Refuse, "chan-stopped:" + mode));
        _parked.Clear();
        _awaitOriginalBoard = null;
    }

    private StabilityRequestRecord Record(string? board, string step, string url, int? status, long bytes, string? sha256,
        string? contentType, string outcome, bool sent, IReadOnlyDictionary<string, string>? headers = null, string? agent = null)
    {
        StabilityRequestRecord row = new(++_sequence, board, step, url, status, bytes, sha256, contentType, outcome, sent, DateTimeOffset.UtcNow, headers, agent);
        _rows.Add(row);
        return row;
    }

    private static char FirstCharacter(byte[] body)
    {
        int index = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        while (index < body.Length && body[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') index++;
        return index < body.Length ? (char)body[index] : '\0';
    }

    private static bool LooksLikeChallenge(byte[] body)
    {
        string text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 65536)).ToLowerInvariant();
        return ChallengeMarkers.Any(text.Contains) || ChallengeTitle.IsMatch(text);
    }

    private static string StaticAgent(string value) => value.Length == 0 ? "(absent)"
        : (value == "MeowChan" || value.StartsWith("MeowChan/", StringComparison.Ordinal)) && value.Length <= 128
            && !value.Any(char.IsControl) ? value : "(unexpected-agent-redacted)";

    private static IReadOnlyDictionary<string, string> SafeResponseHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "Retry-After", "Date", "Content-Length", "Content-Encoding", "Server", "ETag", "Vary", "Via", "Location", "CF-Ray", "CF-Cache-Status" })
            if (response.Headers.TryGetValues(name, out IEnumerable<string>? values) || response.Content.Headers.TryGetValues(name, out values))
            {
                string value = string.Join(", ", values);
                selected[name] = name.Equals("Location", StringComparison.OrdinalIgnoreCase) ? AuditUrl(value) : value[..Math.Min(512, value.Length)];
            }
        return selected;
    }

    internal static string AuditUrl(string value) => LiveSourceAuditTransport.AuditUrl(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _network?.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
