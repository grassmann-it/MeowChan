using System.Net;
using System.IO.Compression;
using System.Net.Http.Headers;
using MeowChan.Http;
using MeowChan.PluginSdk;
using MeowChan.PluginHost.Sources.Booru;

namespace MeowChan.PluginHost.Http;

/// <summary>Metadata HTTP through the session's existing request policy. Transport must disable automatic redirects.</summary>
public sealed class PluginHostHttp : IHostHttp, IDisposable
{
    private static readonly HttpRequestOptionsKey<CancellationToken> CallerCancellationKey = new("MeowChan.MetadataCallerCancellation");
    private readonly HttpClient _transport;
    private readonly HttpClient _borrowedTransport;
    private readonly RequestPolicyState _policy;
    private readonly BooruMetadataHttp _booru;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    public PluginHostHttp(HttpClient transport, RequestPolicyState policy, IReadOnlyList<SourceDescription> sources)
    {
        _borrowedTransport = transport;
        _booru = new BooruMetadataHttp(transport, policy, sources);
        _transport = new HttpClient(new HistoryTransport(transport)) { Timeout = Timeout.InfiniteTimeSpan }; _policy = policy;
        _sources = sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
    }
    public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        => SendCoreAsync(request, null, cancellationToken);

    /// <summary>
    /// Worker host-service entry point. A post-search request is served by the booru job
    /// bound to <paramref name="operation"/> for the whole typed operation (see
    /// <see cref="MetadataHistory.BindOperation"/>); without a live binding it fails closed
    /// instead of falling back to a per-request job.
    /// </summary>
    public Task<HostHttpResult> SendAsync(HostHttpRequest request, Contracts.PluginOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return SendCoreAsync(request, operation, cancellationToken);
    }

    private async Task<HostHttpResult> SendCoreAsync(HostHttpRequest request, Contracts.PluginOperationContext? operation,
        CancellationToken cancellationToken)
    {
        // A final SDK failure may echo only the last completed, non-overlapping
        // host failure. Keep this separate from the operation's deferred history.
        using MetadataHistory.RequestObservation? observation = MetadataHistory.Current?.BeginRequest();
        HostHttpFailure Failure(string code, string message, Exception? evidence = null, HttpStatusCode? responseStatus = null)
        {
            string bounded = message[..Math.Min(message.Length, 2048)];
            FailureDetail detail = new(code, bounded);
            observation?.RecordReturnedFailure(detail, evidence, responseStatus);
            return new(detail);
        }
        if (!_sources.TryGetValue(request.SourceId, out SourceDescription? source))
            return Failure("http-source", "The plugin did not declare this source.");
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateHttpRequest(request, source);
        if (issues.Count > 0) return Failure("http-request", issues[0].Message);
        if (source.Capabilities.HasFlag(SourceCapabilities.PostSearch))
        {
            BooruJob? operationJob = null;
            if (operation is not null)
            {
                operationJob = MetadataHistory.BooruJobFor(operation, _booru.TimeProvider);
                if (operationJob is null)
                    return Failure("booru-job", "The booru job is not live.");
            }
            HostHttpResult booruResult = await _booru.SendAsync(request, source, operationJob, cancellationToken).ConfigureAwait(false);
            if (booruResult is HostHttpFailure booruFailure)
                observation?.RecordReturnedFailure(booruFailure.Failure, null, booruFailure.Failure.HttpStatusCode is int status ? (HttpStatusCode)status : null);
            return booruResult;
        }
        HttpClient client;
        lock (_sync)
        {
            if (!_clients.TryGetValue(request.SourceId, out client!))
            {
                client = new HttpClient(new RequestPolicyHandler(_transport, request.SourceId, _policy)) { Timeout = Timeout.InfiniteTimeSpan };
                if (_borrowedTransport.DefaultRequestHeaders.UserAgent.Count == 0)
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("MeowChan");
                _clients.Add(request.SourceId, client);
            }
        }
        Uri uri = request.Url;
        HttpStatusCode? responseFailureStatus = null;
        using CancellationTokenSource bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                responseFailureStatus = null;
                HostHttpRequest hop = request with { Url = uri };
                issues = SdkValidation.ValidateHttpRequest(hop, source);
                if (issues.Count > 0) return Failure("http-redirect", "Redirect destination is outside the declared metadata hosts.");
                using HttpRequestMessage message = new(request.Method == HostHttpMethod.Head ? HttpMethod.Head : HttpMethod.Get, uri);
                message.Options.Set(RequestPolicyHandler.DeferWhenWaitingKey, true);
                message.Options.Set(CallerCancellationKey, cancellationToken);
                // Capture this operation, not whichever AsyncLocal happens to be current
                // when the policy reports progress on a correlated service continuation.
                if (MetadataHistory.Current is MetadataHistory history)
                    message.Options.Set(RequestPolicyHandler.ObserverKey, history.ReportProgress);
                if (request.Headers != null)
                    foreach ((string key, string value) in request.Headers) message.Headers.TryAddWithoutValidation(key, value);
                // The shared transport does not decompress, so advertise exactly what ReadDecodedBodyAsync decodes
                // (bounded); without this header servers send chan catalogs and thread JSON uncompressed.
                if (message.Headers.AcceptEncoding.Count == 0)
                {
                    message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                    message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                    message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
                }
                if (request.Validators?.EntityTag is string entityTag) message.Headers.TryAddWithoutValidation("If-None-Match", entityTag);
                if (request.Validators?.LastModified is DateTimeOffset modified) message.Headers.IfModifiedSince = modified;
                using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 400 and <= 599) responseFailureStatus = response.StatusCode;
                Uri actual = response.RequestMessage?.RequestUri ?? uri;
                if (actual != uri) return Failure("http-transport", "The supplied metadata transport followed a redirect automatically.", responseStatus: responseFailureStatus);
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
                        return Failure("metadata-too-large", "Metadata body exceeds 8 MiB.", responseStatus: responseFailureStatus);
                    await using Stream body = await ReadDecodedBodyAsync(response.Content, bodyDeadline.Token).ConfigureAwait(false);
                    using MemoryStream buffer = new();
                    byte[] chunk = new byte[65536];
                    int count;
                    while ((count = await body.ReadAsync(chunk, bodyDeadline.Token).ConfigureAwait(false)) != 0)
                    {
                        if (buffer.Length + count > SdkLimits.MaximumMetadataBytes) return Failure("metadata-too-large", "Decompressed metadata body exceeds 8 MiB.", responseStatus: responseFailureStatus);
                        await buffer.WriteAsync(chunk.AsMemory(0, count), bodyDeadline.Token).ConfigureAwait(false);
                    }
                    bytes = buffer.ToArray();
                }
                SourceValidators validators = new(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
                HostHttpResult result = new HostHttpResponse(status, uri, headers, bytes, validators);
                issues = SdkValidation.ValidateHttpResult(request, result, source);
                return issues.Count == 0 ? result : Failure("http-response", issues[0].Message, responseStatus: responseFailureStatus);
            }
            return Failure("http-redirect", "Redirect limit exceeded.");
        }
        catch (RequestPolicyDeferredException deferred)
        {
            MetadataHistory.Current?.AdmittedWait();
            return new HostHttpDeferred(deferred.RetryAt, deferred.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception)
        {
            // Keep the SDK result unchanged. For passive facts only a fired body
            // deadline proves timeout; an unrelated cancellation has no such evidence.
            return Failure("http-timeout", "Metadata body deadline exceeded.",
                bodyDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? new TimeoutException("Metadata body deadline exceeded.") : exception, responseFailureStatus);
        }
        catch (TimeoutException exception) { return Failure("http-timeout", exception.Message, exception, responseFailureStatus); }
        catch (HttpRequestException exception) { return Failure("http-network", exception.Message, exception, responseFailureStatus); }
        catch (InvalidDataException exception) { return Failure("http-encoding", exception.Message, exception, responseFailureStatus); }
        catch (IOException exception) { return Failure("http-network", exception.Message, exception, responseFailureStatus); }
    }
    // Observe the actual transport, not advisory policy reasons (which also occur after retries).
    private sealed class HistoryTransport(HttpClient transport) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            MetadataHistory? history = MetadataHistory.Current;
            history?.Sent();
            try
            {
                using HttpRequestMessage forwarded = new(request.Method, request.RequestUri);
                foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                HttpResponseMessage response = await transport.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 400 and <= 599) history?.Failed(response.StatusCode);
                return response;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or TimeoutException or IOException)
            {
                // The attempt token also includes policy deadlines: it is not caller evidence.
                request.Options.TryGetValue(CallerCancellationKey, out CancellationToken caller);
                if (!caller.IsCancellationRequested) history?.Failed(exception);
                throw;
            }
        }
    }

    internal sealed class MetadataHistory : IDisposable
    {
        private static readonly AsyncLocal<MetadataHistory?> Slot = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Contracts.PluginOperationContext, MetadataHistory> Operations = new();
        private static readonly object OperationsLock = new();
        /// <summary>
        /// Binds this history to <paramref name="operation"/>, replacing an earlier binding of the same operation.
        /// Like every other access to the table it runs under <see cref="OperationsLock"/>, so it cannot race a
        /// concurrent <c>Remove</c> (a plain <c>Add</c> throws when the key exists).
        /// </summary>
        internal void Bind(Contracts.PluginOperationContext operation)
        {
            lock (OperationsLock) Operations.AddOrUpdate(operation, this);
        }

        /// <summary>
        /// Binds a non-ambient history to a typed operation for exactly the caller's scope,
        /// so every host HTTP service call of that operation shares one booru job. The job
        /// is linked to <paramref name="lifetime"/> (caller cancellation, operation deadline,
        /// worker lifetime) and closed when the returned owner is disposed. Returns null when
        /// the caller already bound a live history: that owner keeps the job lifetime.
        /// </summary>
        internal static MetadataHistory? BindOperation(Contracts.PluginOperationContext operation, CancellationToken lifetime)
        {
            ArgumentNullException.ThrowIfNull(operation);
            lock (OperationsLock)
            {
                if (Operations.TryGetValue(operation, out MetadataHistory? existing))
                {
                    if (Volatile.Read(ref existing._disposed) == 0) return null;
                    Operations.Remove(operation);
                }
                MetadataHistory history = new(null, ambient: false) { _operationLifetime = lifetime, _boundOperation = operation };
                Operations.Add(operation, history);
                return history;
            }
        }

        /// <summary>The booru job of the live history bound to <paramref name="operation"/>, if any.</summary>
        internal static BooruJob? BooruJobFor(Contracts.PluginOperationContext operation, TimeProvider clock)
        {
            MetadataHistory? history;
            lock (OperationsLock)
                if (!Operations.TryGetValue(operation, out history) || Volatile.Read(ref history._disposed) != 0) return null;
            return history.GetBooruJob(clock);
        }
        internal static IDisposable Resume(Contracts.PluginOperationContext operation)
        {
            MetadataHistory? previous = Slot.Value;
            MetadataHistory? history;
            lock (OperationsLock)
            {
                if (!Operations.TryGetValue(operation, out MetadataHistory? candidate)
                    || Volatile.Read(ref candidate._disposed) != 0)
                {
                    Operations.Remove(operation);
                    history = null;
                }
                else
                {
                    history = candidate;
                }
            }
            Slot.Value = history;
            return new Restore(previous);
        }
        private sealed class Restore(MetadataHistory? previous) : IDisposable
        {
            public void Dispose() => Slot.Value = previous;
        }
        private readonly MetadataHistory? _previous = Slot.Value;
        private readonly bool _ambient;
        private CancellationToken _operationLifetime;
        private Contracts.PluginOperationContext? _boundOperation;
        private readonly object _progressLock = new();
        private Action<RequestPolicyProgress>? _observer;
        private int _sends, _waits, _disposed;
        private long _requestSequence;
        private int _activeRequests;
        private FailureReceipt? _completedFailure;
        internal static MetadataHistory? Current => Slot.Value is MetadataHistory history
            && Volatile.Read(ref history._disposed) == 0 ? history : null;
        // Preserve the actual parameterless constructor used by existing internal callers.
        internal MetadataHistory() : this(null) { }
        internal MetadataHistory(Action<RequestPolicyProgress>? observer) : this(observer, ambient: true) { }
        private MetadataHistory(Action<RequestPolicyProgress>? observer, bool ambient)
        {
            _observer = observer;
            _ambient = ambient;
            if (ambient) Slot.Value = this;
        }
        internal void ReportProgress(RequestPolicyProgress progress)
        {
            // Serialize delivery with retirement so an in-flight callback cannot write
            // after the adapter's finally clears progress or a successor check starts.
            lock (_progressLock)
                if (_disposed == 0) _observer?.Invoke(progress);
        }
        internal bool InitialNoSend => _sends == 0 && _waits > 0 && Failure == null;
        internal Exception? Failure { get; private set; }
        internal void Sent() => Interlocked.Increment(ref _sends);
        internal void AdmittedWait() => Interlocked.Increment(ref _waits);
        internal void Failed(string code, string message) => Failure = new DiagnosticFailure(
            FailureDiagnostics.FromException(new Contracts.PluginWorkerException(code, message)));
        internal void Failed(HttpStatusCode status) => Failure = new DiagnosticFailure(
            FailureDiagnostics.FromException(new HttpRequestException("Metadata HTTP response failure.", null, status))
                with { DiagnosticCode = $"http-{(int)status}" });
        internal void Failed(Exception exception, HttpStatusCode? responseStatus = null)
        {
            FailureInfo facts = FailureDiagnostics.FromException(exception);
            // A body/validation failure must not erase a failed HTTP response that
            // this very send already observed. Never borrow status from older sends.
            Failure = new DiagnosticFailure(facts with { HttpStatusCode = responseStatus ?? facts.HttpStatusCode });
        }

        internal void BeginSourceResult()
        {
            lock (_progressLock)
            {
                _completedFailure = null;
                ++_requestSequence; // A prior catalog page cannot lend its echo to this result.
            }
        }

        internal RequestObservation? BeginRequest()
        {
            lock (_progressLock)
            {
                if (_disposed != 0) return null;
                _completedFailure = null;
                return new RequestObservation(this, ++_requestSequence, _activeRequests++ == 0);
            }
        }

        internal BooruJob? GetBooruJob(TimeProvider clock)
        {
            lock (_progressLock)
            {
                if (_disposed != 0) return null;
                return _booruJob ??= new BooruJob(clock, _operationLifetime);
            }
        }
        private BooruJob? _booruJob;

        internal FailureInfo? MatchFinalFailure(FailureDetail final)
        {
            lock (_progressLock)
                return _disposed == 0 && _activeRequests == 0 && _completedFailure is FailureReceipt completed
                    && completed.Code == final.Code && completed.Status == final.HttpStatusCode
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        completed.MessageDigest, MessageDigest(final.Message)) ? completed.Facts : null;
        }

        // Exact echo comparison survives SDK serialization without retaining the raw
        // message. Hash UTF-16 code units (including malformed surrogate sequences).
        private static byte[] MessageDigest(string message) => System.Security.Cryptography.SHA256.HashData(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(message.AsSpan()));
        private sealed record FailureReceipt(string Code, int? Status, byte[] MessageDigest, FailureInfo Facts);

        internal sealed class RequestObservation(MetadataHistory owner, long sequence, bool exclusive) : IDisposable
        {
            private FailureReceipt? _failure;
            private bool _disposed;
            internal void RecordReturnedFailure(FailureDetail detail, Exception? evidence, HttpStatusCode? responseStatus)
            {
                FailureInfo facts = FailureDiagnostics.FromException(evidence
                    ?? new Contracts.PluginWorkerException(detail.Code, detail.Message));
                DiagnosticFailure diagnostic = new(facts with { HttpStatusCode = responseStatus ?? facts.HttpStatusCode });
                lock (owner._progressLock)
                {
                    if (_disposed || owner._disposed != 0) return;
                    // Preserve the existing post-failure-deferred observation independently.
                    owner.Failure = diagnostic;
                    _failure = new(detail.Code, detail.HttpStatusCode, MessageDigest(detail.Message), diagnostic.FailureInfo);
                }
            }
            public void Dispose()
            {
                lock (owner._progressLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    owner._activeRequests--;
                    if (owner._disposed == 0 && exclusive && sequence == owner._requestSequence && owner._activeRequests == 0)
                        owner._completedFailure = _failure;
                }
            }
        }

        // Keep only the mapper's facts, never the raw exception/inner chain, URL,
        // body or message. Failure remains Exception for existing adapter consumers.
        private sealed class DiagnosticFailure : Exception, IFailureInfoProvider
        {
            public FailureInfo FailureInfo { get; }
            internal DiagnosticFailure(FailureInfo facts) : base("Metadata request failed.")
                => FailureInfo = facts with { Message = Message };
        }
        public void Dispose()
        {
            // Correlated service tasks may arrive after the parent operation
            // retires. A retained context must never resurrect its history.
            BooruJob? booruJob;
            lock (_progressLock)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                // A retained operation/callback must not keep the watch observer alive.
                _observer = null;
                _completedFailure = null;
                booruJob = _booruJob;
                _booruJob = null;
            }
            booruJob?.Close();
            if (_boundOperation is Contracts.PluginOperationContext operation)
                lock (OperationsLock)
                    if (Operations.TryGetValue(operation, out MetadataHistory? bound) && ReferenceEquals(bound, this))
                        Operations.Remove(operation);
            if (_ambient) Slot.Value = _previous;
        }
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
    public void Dispose() { lock (_sync) { foreach (HttpClient client in _clients.Values) client.Dispose(); _clients.Clear(); _booru.Dispose(); _transport.Dispose(); } }

    /// <summary>Creates a transport with visible response bytes and no invisible redirects/retries.</summary>
    public static HttpClient CreateTransport() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        // The shared transport must not silently attach profile/browser credentials to
        // public provider metadata. BooruHostHttp also rejects explicit credential
        // headers before the handler is entered.
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null,
        // Keep wire bytes observable for the Booru path. PluginHostHttp performs
        // explicit bounded decoding for ordinary metadata and advertises
        // Accept-Encoding itself; OriginalMedia requests advertise identity and
        // therefore keep their payload bytes unchanged.
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };
}
