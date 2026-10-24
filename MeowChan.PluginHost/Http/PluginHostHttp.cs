using System.Net;
using System.IO.Compression;
using System.Net.Http.Headers;
using MeowChan.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

/// <summary>Metadata HTTP through the session's existing request policy. Transport must disable automatic redirects.</summary>
public sealed class PluginHostHttp : IHostHttp, IDisposable
{
    private readonly HttpClient _transport;
    private readonly RequestPolicyState _policy;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    public PluginHostHttp(HttpClient transport, RequestPolicyState policy, IReadOnlyList<SourceDescription> sources)
    {
        _transport = transport; _policy = policy;
        _sources = sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
    }
    public async Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!_sources.TryGetValue(request.SourceId, out SourceDescription? source))
            return Failure("http-source", "The plugin did not declare this source.");
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateHttpRequest(request, source);
        if (issues.Count > 0) return Failure("http-request", issues[0].Message);
        HttpClient client;
        lock (_sync)
        {
            if (!_clients.TryGetValue(request.SourceId, out client!))
            {
                client = new HttpClient(new RequestPolicyHandler(_transport, request.SourceId, _policy)) { Timeout = Timeout.InfiniteTimeSpan };
                if (_transport.DefaultRequestHeaders.UserAgent.Count == 0)
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("MeowChan");
                _clients.Add(request.SourceId, client);
            }
        }
        Uri uri = request.Url;
        using CancellationTokenSource bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                HostHttpRequest hop = request with { Url = uri };
                issues = SdkValidation.ValidateHttpRequest(hop, source);
                if (issues.Count > 0) return Failure("http-redirect", "Redirect destination is outside the declared metadata hosts.");
                using HttpRequestMessage message = new(request.Method == HostHttpMethod.Head ? HttpMethod.Head : HttpMethod.Get, uri);
                message.Options.Set(RequestPolicyHandler.DeferWhenWaitingKey, true);
                if (request.Headers != null)
                    foreach ((string key, string value) in request.Headers) message.Headers.TryAddWithoutValidation(key, value);
                if (request.Validators?.EntityTag is string entityTag) message.Headers.TryAddWithoutValidation("If-None-Match", entityTag);
                if (request.Validators?.LastModified is DateTimeOffset modified) message.Headers.IfModifiedSince = modified;
                using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                Uri actual = response.RequestMessage?.RequestUri ?? uri;
                if (actual != uri) return Failure("http-transport", "The supplied metadata transport followed a redirect automatically.");
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirect == 5 || response.Headers.Location == null) return Failure("http-redirect", "Metadata redirect limit exceeded or Location missing.");
                    uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                    continue;
                }
                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                foreach (string name in new[] { "ETag", "Last-Modified", "Content-Type", "Retry-After", "Cache-Control", "Date" })
                {
                    if (response.Headers.TryGetValues(name, out IEnumerable<string>? values) || response.Content.Headers.TryGetValues(name, out values))
                        headers[name] = string.Join(", ", values);
                }
                byte[] bytes = [];
                if (request.Method != HostHttpMethod.Head && status != 304)
                {
                    bodyDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    if (response.Content.Headers.ContentLength > SdkLimits.MaximumMetadataBytes)
                        return Failure("metadata-too-large", "Metadata body exceeds 8 MiB.");
                    await using Stream body = await ReadDecodedBodyAsync(response.Content, bodyDeadline.Token).ConfigureAwait(false);
                    using MemoryStream buffer = new();
                    byte[] chunk = new byte[65536];
                    int count;
                    while ((count = await body.ReadAsync(chunk, bodyDeadline.Token).ConfigureAwait(false)) != 0)
                    {
                        if (buffer.Length + count > SdkLimits.MaximumMetadataBytes) return Failure("metadata-too-large", "Decompressed metadata body exceeds 8 MiB.");
                        await buffer.WriteAsync(chunk.AsMemory(0, count), bodyDeadline.Token).ConfigureAwait(false);
                    }
                    bytes = buffer.ToArray();
                }
                SourceValidators validators = new(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
                HostHttpResult result = new HostHttpResponse(status, uri, headers, bytes, validators);
                issues = SdkValidation.ValidateHttpResult(request, result, source);
                return issues.Count == 0 ? result : Failure("http-response", issues[0].Message);
            }
            return Failure("http-redirect", "Redirect limit exceeded.");
        }
        catch (RequestPolicyDeferredException deferred) { return new HostHttpDeferred(deferred.RetryAt, deferred.ReasonCode); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure("http-timeout", "Metadata body deadline exceeded."); }
        catch (TimeoutException exception) { return Failure("http-timeout", exception.Message); }
        catch (HttpRequestException exception) { return Failure("http-network", exception.Message); }
        catch (InvalidDataException exception) { return Failure("http-encoding", exception.Message); }
        catch (IOException exception) { return Failure("http-network", exception.Message); }
    }
    private static async Task<Stream> ReadDecodedBodyAsync(HttpContent content, CancellationToken token)
    {
        Stream body = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        try
        {
            if (content.Headers.ContentEncoding.Count > 2) throw new InvalidDataException("Too many content encodings.");
            foreach (string encoding in content.Headers.ContentEncoding.Reverse())
                body = encoding.ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(body, CompressionMode.Decompress),
                    "deflate" => new DeflateStream(body, CompressionMode.Decompress),
                    "br" => new BrotliStream(body, CompressionMode.Decompress),
                    "identity" => body,
                    _ => throw new InvalidDataException("Unsupported metadata content encoding.")
                };
            return body;
        }
        catch { await body.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private static HostHttpFailure Failure(string code, string message) => new(new(code, message[..Math.Min(message.Length, 2048)]));
    public void Dispose() { lock (_sync) { foreach (HttpClient client in _clients.Values) client.Dispose(); _clients.Clear(); } }

    /// <summary>Creates a transport with explicit decompression and no invisible redirects/retries.</summary>
    public static HttpClient CreateTransport() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = Timeout.InfiniteTimeSpan };
}
