using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using MeowChan.PluginHost.Http;

namespace MeowChan.DesktopTests;

internal sealed record LiveSourceSelection(string SourceId, string PluginId, string BoardId, string ThreadId,
    string BoardUrl, string ThreadUrl, string CatalogUrl, string ThreadMetadataUrl, string OriginalUrl,
    string MediaId, string PostId, int Position, long ExpectedBytes, string Extension, string? ExpectedMd5Base64 = null);

internal sealed class LiveHarnessLimitException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed record LiveHttpAudit(string Kind, string Url, int? Status, long Bytes, string? Sha256, string Outcome, string? ContentType = null, IReadOnlyDictionary<string, string>? ResponseHeaders = null, string? EffectiveUserAgent = null);

// Only this handler can call the real transport. It never manufactures an HTTP response or changes payload bytes.
internal sealed class LiveSourceAuditTransport : HttpMessageHandler
{
    internal const int MaximumOriginalBytes = 2 * 1024 * 1024;
    internal const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private readonly LiveSourceSelection _selection;
    private readonly HttpClient _network;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    internal ConcurrentQueue<LiveHttpAudit> Audit { get; } = new();
    internal bool RejectAll { get; init; }
    internal Func<CancellationToken, Task>? BeforeOriginal { get; set; }
    internal int ActualNetworkSends;
    internal string? OriginalSha256 { get; private set; }
    internal string? OriginalMd5Base64 { get; private set; }
    internal long OriginalBytes { get; private set; }

    internal LiveSourceAuditTransport(LiveSourceSelection selection, HttpClient? network = null)
    {
        _selection = selection;
        if (selection.ExpectedBytes is <= 0 or > MaximumOriginalBytes || selection.Position < 0)
            throw new ArgumentException("Invalid bounded original selection.");
        foreach (string url in new[] { selection.CatalogUrl, selection.ThreadMetadataUrl, selection.OriginalUrl })
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || !uri.IsDefaultPort
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Live URLs must be explicit public HTTPS endpoints without credentials/query/fragment.");
        if (new[] { selection.CatalogUrl, selection.ThreadMetadataUrl, selection.OriginalUrl }.Distinct().Count() != 3)
            throw new ArgumentException("Three distinct endpoints are required.");
        _network = network ?? PluginHostHttp.CreateTransport();
    }

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string url = request.RequestUri?.AbsoluteUri ?? "missing";
        string kind = url == _selection.CatalogUrl ? "catalog" : url == _selection.ThreadMetadataUrl ? "thread"
            : url == _selection.OriginalUrl ? "original" : "unselected";
        string? denial = null;
        lock (_gate)
        {
            if (RejectAll) denial = "offline-request";
            else if (request.Method != HttpMethod.Get || request.Headers.Range != null) denial = "method-or-range";
            else if (kind == "unselected") denial = "unselected-url";
            else if (!_consumed.Add(url)) denial = "duplicate-request";
        }
        if (denial != null)
        {
            Audit.Enqueue(new(kind, AuditUrl(url), null, 0, null, "harness-limit:" + denial));
            throw new LiveHarnessLimitException(denial);
        }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        if (kind == "original")
        {
            if (BeforeOriginal == null)
            {
                Audit.Enqueue(new(kind, AuditUrl(url), null, 0, null, "harness-limit:missing-parsed-attachment-gate"));
                throw new LiveHarnessLimitException("missing-parsed-attachment-gate");
            }
            try { await BeforeOriginal(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                Audit.Enqueue(new(kind, AuditUrl(url), null, 0, null, error is OperationCanceledException ? "timeout-or-cancelled" : "harness-limit:selected-attachment-mismatch"));
                throw;
            }
        }
        int maximum = kind == "original" ? MaximumOriginalBytes : MaximumMetadataBytes;
        int? status = null;
        string? contentType = null;
        IReadOnlyDictionary<string, string>? responseHeaders = null;
        string? effectiveUserAgent = null;
        long received = 0;
        // An HttpClient has already marked the incoming message sent. Clone its GET and headers for the real client.
        using HttpRequestMessage outbound = new(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) outbound.Headers.TryAddWithoutValidation(header.Key, header.Value);
        HttpResponseMessage? response = null;
        try
        {
            effectiveUserAgent = StaticAgent(outbound.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : _network.DefaultRequestHeaders.UserAgent.ToString());
            Interlocked.Increment(ref ActualNetworkSends);
            response = await _network.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            contentType = response.Content.Headers.ContentType?.ToString();
            responseHeaders = SafeResponseHeaders(response);
            effectiveUserAgent = StaticAgent(response.RequestMessage?.Headers.UserAgent.ToString() ?? "");
            if (response.RequestMessage?.RequestUri != outbound.RequestUri)
                throw new LiveHarnessLimitException("unexpected-final-uri");
            // Redirects remain real HTTP results. The next hop cannot escape the exact allowlist or one-send budget.
            byte[] bytes = await ReadBoundedAsync(response.Content, maximum, deadline.Token, count => received += count).ConfigureAwait(false);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var old = response.Content;
            ByteArrayContent buffered = new(bytes);
            foreach (var header in old.Headers) buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = buffered;
            old.Dispose();
            Audit.Enqueue(new(kind, AuditUrl(url), status, bytes.Length, hash, status == 200 ? "http-200" : "http-status", contentType, responseHeaders, effectiveUserAgent));
            if (kind == "original" && status == 200)
            {
                OriginalBytes = bytes.Length;
                OriginalSha256 = hash;
                OriginalMd5Base64 = Convert.ToBase64String(MD5.HashData(bytes));
            }
            return response;
        }
        catch (Exception error)
        {
            response?.Dispose();
            string outcome = error is LiveHarnessLimitException limit ? "harness-limit:" + limit.Code
                : error is OperationCanceledException ? "timeout-or-cancelled" : "transport-error:" + error.GetType().Name;
            Audit.Enqueue(new(kind, AuditUrl(url), status, received, null, outcome, contentType, responseHeaders, effectiveUserAgent));
            throw;
        }
    }

    private static string StaticAgent(string value) => value.Length == 0 ? "(absent)"
        : (value == "MeowChan" || value.StartsWith("MeowChan/", StringComparison.Ordinal)) && value.Length <= 128
            && !value.Any(char.IsControl) ? value : "(unexpected-agent-redacted)";

    private static IReadOnlyDictionary<string, string> SafeResponseHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "Retry-After", "Date", "Content-Length", "Content-Encoding", "Server", "ETag", "Vary", "Via", "CF-Polished" })
            if (response.Headers.TryGetValues(name, out IEnumerable<string>? values)
                || response.Content.Headers.TryGetValues(name, out values))
            {
                string value = string.Join(", ", values);
                selected[name] = value[..Math.Min(512, value.Length)];
            }
        return selected;
    }

    internal static string AuditUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return "invalid-uri";
        UriBuilder redacted = new(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return redacted.Uri.GetLeftPart(UriPartial.Path);
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token, Action<int>? onRead = null)
    {
        long? declared = content.Headers.ContentLength;
        if (declared > maximum) throw new LiveHarnessLimitException("declared-body-too-large");
        await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] chunk = new byte[Math.Min(65536, maximum)];
        while (output.Length < maximum)
        {
            int remaining = maximum - checked((int)output.Length);
            int count = await input.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            onRead?.Invoke(count);
            await output.WriteAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false);
        }
        // Never consume a cap+1 byte. Exact-cap unknown/chunked bodies conservatively fail without an EOF proof.
        if (declared == maximum) return output.ToArray();
        throw new LiveHarnessLimitException("body-reached-limit-without-eof");
    }

    internal void CancelPending() => _lifetime.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _network.Dispose(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
