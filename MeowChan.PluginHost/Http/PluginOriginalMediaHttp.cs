using MeowChan.Http;
using MeowChan.PluginSdk;
using Serilog;

namespace MeowChan.PluginHost.Http;

/// <summary>Original-media GET/HEAD requests with explicit, source-declared redirect destinations.</summary>
public static class PluginOriginalMediaHttp
{
    public const int MaximumRedirects = 5;

    /// <summary>
    /// The returned client owns its policy/redirect wrappers, never the borrowed transport.
    /// Transport must disable automatic redirects/retries and must not carry authentication headers.
    /// All redirect headers share one 60-second deadline; body deadlines remain with DownloadWorker.
    /// </summary>
    public static HttpClient CreateClient(HttpClient transport, SourceDescription source, RequestPolicyState policy, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);
        return new HttpClient(new RedirectHandler(transport, source, policy, logger)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Accept", "Accept-Encoding", "Accept-Language", "User-Agent" };
        private readonly HttpClient _transport;
        private readonly HttpClient _checkedTransport;
        private readonly HttpClient _client;
        private readonly HashSet<string> _hosts;
        private readonly TimeProvider _clock;

        internal RedirectHandler(HttpClient transport, SourceDescription source, RequestPolicyState policy, ILogger? logger)
        {
            _transport = transport;
            // RequestPolicyHandler replaces response.RequestMessage with its
            // caller's message. Check the raw destination before that rewrite.
            _checkedTransport = new HttpClient(new TransportGuard(transport)) { Timeout = Timeout.InfiniteTimeSpan };
            _client = ProviderHttpClientFactory.CreatePolicyClient(_checkedTransport, source.SourceId, policy, logger);
            _hosts = source.HttpHosts.Where(host => host.Purpose.HasFlag(HttpHostPurpose.OriginalMedia))
                .Select(host => host.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _clock = policy.TimeProvider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Content != null || request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
                throw new HttpRequestException("Original media supports only bodyless GET/HEAD requests.");
            if (request.Headers.Any(header => !SafeHeaders.Contains(header.Key))
                || _transport.DefaultRequestHeaders.Any(header => !SafeHeaders.Contains(header.Key)))
                throw new HttpRequestException("Original media request headers must not carry credentials or unsupported headers.");
            Uri uri = request.RequestUri ?? throw new HttpRequestException("An absolute original-media URI is required.");
            Validate(uri);
            using CancellationTokenSource deadline = new(RequestPolicyHandler.OverallTimeout, _clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    Validate(uri);
                    using HttpRequestMessage hop = new(request.Method, uri)
                    { Version = request.Version, VersionPolicy = request.VersionPolicy };
                    foreach (var header in request.Headers) hop.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    foreach (var option in request.Options) hop.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                    HttpResponseMessage response = await _client.SendAsync(hop, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if ((response.RequestMessage?.RequestUri ?? uri) != uri)
                            throw new HttpRequestException("Original-media transport followed an unvalidated redirect.");
                        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
                        if (redirect == MaximumRedirects || response.Headers.Location == null)
                            throw new HttpRequestException("Original-media redirect limit exceeded or Location missing.");
                        Uri next;
                        try { next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location); }
                        catch (UriFormatException exception) { throw new HttpRequestException("Invalid original-media redirect destination.", exception); }
                        Validate(next);
                        if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                            throw new HttpRequestException("Original-media redirects cannot downgrade HTTPS.");
                        uri = next;
                    }
                    catch { response.Dispose(); throw; }
                    response.Dispose();
                }
                throw new HttpRequestException("Original-media redirect limit exceeded.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
            { throw new TimeoutException("Original-media redirect headers exceeded their shared 60-second deadline.", exception); }
            catch (UnvalidatedTransportRedirectException exception)
            { throw new HttpRequestException("Original-media transport followed an unvalidated redirect.", exception); }
        }

        private void Validate(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
                || uri.Fragment.Length != 0 || !uri.IsDefaultPort || !_hosts.Contains(uri.IdnHost))
                throw new HttpRequestException("Original-media destination must use a declared original-media host without credentials, fragment or custom port.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _client.Dispose(); _checkedTransport.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class UnvalidatedTransportRedirectException : Exception { }

    private sealed class TransportGuard(HttpClient transport) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using HttpRequestMessage raw = new(request.Method, request.RequestUri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) raw.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var option in request.Options) raw.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            HttpResponseMessage response = await transport.SendAsync(raw, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((response.RequestMessage?.RequestUri ?? request.RequestUri) != request.RequestUri)
                    throw new UnvalidatedTransportRedirectException();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }
}
