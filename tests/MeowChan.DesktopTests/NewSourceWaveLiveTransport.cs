using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using MeowChan.PluginHost.Http;

namespace MeowChan.DesktopTests;

internal sealed class NewSourceWaveLiveLimitException(string code) : Exception(code);
internal sealed record NewSourceWaveLiveHttp(int Attempt, string Phase, string Kind, string Url, int? Status,
    long Bytes, string? Sha256, string? Md5, string? Sha1, string Outcome, string? Location = null,
    string? ContentType = null, string? UserAgent = null, DateTimeOffset StartedUtc = default)
{
    public DateTimeOffset FinishedUtc { get; } = DateTimeOffset.UtcNow;
}

// This is the sole socket boundary. It preserves real responses/redirects and payloads. Every
// physical hop consumes the same eight-send budget; HttpClient automatic redirects are disabled.
internal sealed class NewSourceWaveLiveTransport : HttpMessageHandler
{
    internal const int MaximumOriginalBytes = 256 * 1024;
    internal const int MaximumMetadataBytes = 8 * 1024 * 1024;
    internal const int MaximumSendAttempts = 8;
    private readonly NewSourceWaveLiveSelection _selection;
    private readonly HttpClient _network;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private int _attempts;
    private bool _retryArmed;
    internal bool RejectAll { get; init; }
    internal Func<CancellationToken, Task>? BeforeOriginal { get; set; }
    internal ConcurrentQueue<NewSourceWaveLiveHttp> Audit { get; } = new();
    internal int ActualNetworkSends => Volatile.Read(ref _attempts);
    internal NewSourceWaveLiveHttp[] OriginalBodies => Audit.Where(row => row.Kind == "original" && row.Status == 200 && row.Outcome == "http-200").ToArray();
    internal NewSourceWaveLiveTransport(NewSourceWaveLiveSelection selection)
    {
        selection.Validate(); _selection = selection;
        _network = PluginHostHttp.CreateTransport();
    }
    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    internal void ArmExplicitConsentRetry()
    {
        lock (_gate)
        {
            if (_selection.SourceId != "leftypol" || _retryArmed || OriginalBodies.Length != 1)
                throw new NewSourceWaveLiveLimitException("consent-retry-not-available");
            _retryArmed = true;
        }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string url = request.RequestUri?.AbsoluteUri ?? "missing";
        int hop = Array.IndexOf(_selection.OriginalHops, url);
        string kind = url == _selection.CatalogUrl ? "catalog" : url == _selection.ThreadMetadataUrl ? "thread" : hop >= 0 ? "original" : "unselected";
        string phase;
        string? denial = null;
        lock (_gate)
        {
            phase = _retryArmed ? "consent-retry" : "strict";
            string key = kind == "original" ? phase + ":" + url : url;
            if (RejectAll) denial = "offline-request";
            else if (request.Method != HttpMethod.Get || request.Headers.Range != null) denial = "method-or-range";
            else if (kind == "unselected") denial = "unselected-url";
            else if (!_consumed.Add(key)) denial = "duplicate-request";
            else if (_attempts >= MaximumSendAttempts) denial = "network-send-budget";
            else if (hop > 0 && !Audit.Any(row => row.Phase == phase && row.Url == _selection.OriginalHops[hop - 1]
                && row.Status is 301 or 302 or 303 or 307 or 308 && row.Location == url)) denial = "redirect-hop-not-observed";
        }
        if (denial != null)
        {
            Audit.Enqueue(new(0, phase, kind, url, null, 0, null, null, null, "harness-limit:" + denial, StartedUtc: started));
            throw new NewSourceWaveLiveLimitException(denial);
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        int attempt = 0; int? status = null; long received = 0;
        string? location = null, type = null, agent = null;
        HttpResponseMessage? response = null;
        try
        {
            if (hop == 0)
            {
                if (BeforeOriginal == null) throw new NewSourceWaveLiveLimitException("missing-parsed-attachment-gate");
                await BeforeOriginal(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            using HttpRequestMessage outbound = new(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) outbound.Headers.TryAddWithoutValidation(header.Key, header.Value);
            agent = outbound.Headers.UserAgent.ToString();
            if (agent.Length > 128 || agent.Any(char.IsControl)) agent = "(redacted)";
            lock (_gate)
            {
                if (_attempts >= MaximumSendAttempts) throw new NewSourceWaveLiveLimitException("network-send-budget");
                attempt = ++_attempts;
            }
            response = await _network.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            type = response.Content.Headers.ContentType?.ToString();
            if (response.RequestMessage?.RequestUri != outbound.RequestUri) throw new NewSourceWaveLiveLimitException("automatic-redirect-or-uri-change");
            if (response.Headers.Location is { } redirect) location = new Uri(outbound.RequestUri!, redirect).AbsoluteUri;
            int cap = kind == "original" ? MaximumOriginalBytes : MaximumMetadataBytes;
            byte[] bytes = await ReadBoundedAsync(response.Content, cap, timeout.Token, count => received += count).ConfigureAwait(false);
            string sha256 = Hash(SHA256.HashData(bytes));
            string md5 = Convert.ToBase64String(MD5.HashData(bytes));
            string sha1 = Convert.ToBase64String(SHA1.HashData(bytes));
            var old = response.Content;
            ByteArrayContent buffered = new(bytes);
            foreach (var header in old.Headers) buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = buffered; old.Dispose();
            Audit.Enqueue(new(attempt, phase, kind, url, status, bytes.Length, sha256, md5, sha1,
                status == 200 ? "http-200" : "http-status", location, type, agent, started));
            return response;
        }
        catch (Exception error)
        {
            response?.Dispose();
            Audit.Enqueue(new(attempt, phase, kind, url, status, received, null, null, null,
                error is NewSourceWaveLiveLimitException ? "harness-limit:" + error.Message
                : error is OperationCanceledException ? "timeout-or-cancelled" : "transport-error:" + error.GetType().Name,
                location, type, agent, started));
            throw;
        }
    }
    private static string Hash(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token, Action<int> count)
    {
        if (content.Headers.ContentLength > maximum) throw new NewSourceWaveLiveLimitException("declared-body-too-large");
        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream buffer = new(); byte[] chunk = new byte[65536];
        while (buffer.Length < maximum)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum - checked((int)buffer.Length))), token).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            count(read); await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (content.Headers.ContentLength == maximum) return buffer.ToArray();
        throw new NewSourceWaveLiveLimitException("body-at-cap-without-eof");
    }
    internal void CancelPending() => _lifetime.Cancel();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _network.Dispose(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
