using System.Collections.Concurrent;
using System.Net;

namespace MeowChan.DesktopTests;

// A NEW opt-in live experiment, not a replacement for the historical one-send audit.
// The old transport still owns exact URLs, bounded complete buffering and real HTTP.
// This wrapper permits only one deliberate second original after an observed strict failure.
// Arming a test phase never grants a production permission: the callback must inspect
// the actual host permission/reference immediately before that second network request.
internal sealed class SourceIntegrityLiveTransport : HttpMessageHandler
{
    private readonly LiveSourceSelection _selection;
    private readonly LiveSourceAuditTransport _strict;
    private readonly LiveSourceAuditTransport _accepted;
    private readonly HttpClient _strictClient;
    private readonly HttpClient _acceptedClient;
    private readonly object _gate = new();
    private bool _firstAttempted, _secondArmed, _secondAttempted;
    internal ConcurrentQueue<LiveHttpAudit> Denials { get; } = new();
    internal bool RejectAll { get; init; }
    internal LiveHttpAudit[] StrictAudit => _strict.Audit.ToArray();
    internal LiveHttpAudit[] AcceptedAudit => _accepted.Audit.ToArray();
    internal int ActualNetworkSends => _strict.ActualNetworkSends + _accepted.ActualNetworkSends;
    internal string? StrictOriginalSha256 => _strict.OriginalSha256;
    internal string? AcceptedOriginalSha256 => _accepted.OriginalSha256;
    internal string? OriginalMd5Base64 => _strict.OriginalMd5Base64;
    internal long OriginalBytes => _strict.OriginalBytes;
    internal bool SameOriginalBodies => _strict.OriginalSha256 != null && _accepted.OriginalSha256 != null
        && _strict.OriginalBytes == _accepted.OriginalBytes
        && _strict.OriginalSha256 == _accepted.OriginalSha256
        && _strict.OriginalMd5Base64 == _accepted.OriginalMd5Base64;

    internal SourceIntegrityLiveTransport(LiveSourceSelection selection,
        HttpClient? strictNetwork = null, HttpClient? acceptedNetwork = null)
    {
        _selection = selection;
        _strict = new(selection, strictNetwork);
        _accepted = new(selection, acceptedNetwork);
        _strictClient = _strict.Client();
        _acceptedClient = _accepted.Client();
    }

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    internal Func<CancellationToken, Task>? BeforeStrictOriginal { set => _strict.BeforeOriginal = value; }

    internal void ArmSecondAfterObservedStrictFailure(Func<CancellationToken, Task> inspectCurrentExplicitHostPermission)
    {
        ArgumentNullException.ThrowIfNull(inspectCurrentExplicitHostPermission);
        lock (_gate)
        {
            if (_secondArmed || _secondAttempted || _strict.OriginalSha256 == null
                || _strict.Audit.Count(row => row.Kind == "original" && row.Status == 200) != 1)
                throw new LiveHarnessLimitException("second-original-not-ready");
            _accepted.BeforeOriginal = inspectCurrentExplicitHostPermission;
            _secondArmed = true;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string url = request.RequestUri?.AbsoluteUri ?? "missing";
        string kind = url == _selection.CatalogUrl ? "catalog" : url == _selection.ThreadMetadataUrl ? "thread"
            : url == _selection.OriginalUrl ? "original" : "unselected";
        bool accepted = false;
        string? denial = null;
        lock (_gate)
        {
            if (RejectAll) denial = "offline-request";
            else if (request.Method != HttpMethod.Get || request.Headers.Range != null) denial = "method-or-range";
            else if (kind == "unselected") denial = "unselected-url";
            else if (kind == "original")
            {
                if (!_firstAttempted) _firstAttempted = true;
                else if (!_secondArmed) denial = "explicit-second-original-not-armed";
                else if (_secondAttempted) denial = "duplicate-request";
                else accepted = _secondAttempted = true;
            }
        }
        if (denial != null)
        {
            Denials.Enqueue(new(kind, LiveSourceAuditTransport.AuditUrl(url), null, 0, null, "harness-limit:" + denial));
            throw new LiveHarnessLimitException(denial);
        }
        // HttpClient already marked the incoming request sent. Forward a fresh GET;
        // do not alter the provider URL, headers, response body or integrity metadata.
        using HttpRequestMessage copy = new(request.Method, request.RequestUri)
        { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        HttpResponseMessage response = await (accepted ? _acceptedClient : _strictClient)
            .SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (accepted && response.StatusCode == HttpStatusCode.OK && !SameOriginalBodies)
        {
            response.Dispose();
            Denials.Enqueue(new(kind, LiveSourceAuditTransport.AuditUrl(url), 200,
                _accepted.OriginalBytes, _accepted.OriginalSha256, "harness-limit:original-body-changed-between-phases"));
            throw new LiveHarnessLimitException("original-body-changed-between-phases");
        }
        return response;
    }

    internal void CancelPending() { _strict.CancelPending(); _accepted.CancelPending(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _strictClient.Dispose(); _acceptedClient.Dispose();
            _strict.Dispose(); _accepted.Dispose();
        }
        base.Dispose(disposing);
    }
}
