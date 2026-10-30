using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Downloader;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources;

internal sealed class PluginSourceBoard : BaseBoard
{
    private readonly PluginSourceImageBoard _source;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private SourceValidators? _validators;
    private IReadOnlyList<IThread>? _lastCatalog;
    private DateTimeOffset? _retryAt;
    private SourceCheckDeferredException? _deferredHistory;
    private Exception? _retryFailure;
    private TimeProvider Clock => SessionQueue?.Clock ?? TimeProvider.System;
    internal PluginSourceBoard(PluginSourceImageBoard source, string board, Uri canonical)
    { _source = source; BoardCode = board; BoardUri = canonical; Name = board; }
    public override IImageBoard ImageBoard => _source;
    public override string BoardCode { get; }
    public override string Name { get; protected set; }
    public override string NiceName => $"/{BoardCode}/ - {Name}";
    public override Uri BoardUri { get; }

    public override async Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource lifetime = _source.LinkLifetime(cancellationToken);
            cancellationToken = lifetime.Token;
            if (_retryAt is DateTimeOffset due && due > Clock.GetUtcNow())
                throw _deferredHistory ?? new SourceCheckDeferredException(due, "source_failure_retry", false, _retryFailure);
            using var history = new Http.PluginHostHttp.MetadataHistory();
            var operation = new PluginOperationContext(BoardUri.AbsoluteUri, WatchGeneration);
            history.Bind(operation);
            CatalogReadRequest request = new(new SourceBoardId(_source.Name, BoardCode), Validators: _validators, HasSnapshot: _lastCatalog != null);
            CatalogReadResult result = await ReadPagesAsync(request, operation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            switch (result)
            {
                case CatalogSnapshot snapshot:
                    List<IThread> threads = new();
                    foreach (SourceThreadReference reference in snapshot.Snapshot.Threads)
                    {
                        if (!_source.Router.TryResolve(reference.Url, out SourceResourceKind kind, out string board, out string? id, out Uri? canonical)
                            || kind != SourceResourceKind.Thread || board != BoardCode || id != reference.Id.ThreadId)
                            throw new PluginWorkerException("invalid_source_result", "Catalog URL does not match its declared thread identity.");
                        threads.Add(new PluginSourceThread(this, id, canonical!)
                        {
                            Title = reference.Title ?? string.Empty,
                            CatalogOpeningText = reference.OpeningTextFormat == SourceTextFormat.Html
                                ? ArchiveText.PlainText(reference.OpeningText) : reference.OpeningText ?? string.Empty
                        });
                    }
                    _validators = snapshot.Snapshot.Validators;
                    _retryAt = null;
                    _deferredHistory = null;
                    _retryFailure = null;
                    _lastCatalog = threads.AsReadOnly();
                    return _lastCatalog;
                case CatalogUnchanged unchanged:
                    _retryAt = null;
                    _deferredHistory = null;
                    _retryFailure = null;
                    _validators = unchanged.Validators;
                    return _lastCatalog!;
                case CatalogDeferred deferred:
                    _retryAt = history.InitialNoSend || deferred.RetryAt > Clock.GetUtcNow().AddSeconds(10)
                        ? deferred.RetryAt : Clock.GetUtcNow().AddSeconds(10);
                    throw _deferredHistory = new SourceCheckDeferredException(_retryAt.Value, deferred.ReasonCode, history.InitialNoSend, history.Failure);
                case CatalogFailure failure:
                    _retryAt = failure.Failure.RetryAt is DateTimeOffset retry && retry > Clock.GetUtcNow().AddSeconds(10)
                        ? retry : Clock.GetUtcNow().AddSeconds(10);
                        _deferredHistory = null;
                    throw _retryFailure = new PluginWorkerException(failure.Failure.Code, failure.Failure.Message);
                default: throw new PluginWorkerException("invalid_source_result", "Unsupported catalog result.");
            }
        }
        finally { _readGate.Release(); }
    }

    private async Task<CatalogReadResult> ReadPagesAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken token)
    {
        List<SourceThreadReference> threads = new();
        HashSet<SourceThreadId> identities = new();
        HashSet<(string? Cursor, int? Page)> pages = new();
        SourceValidators? firstValidators = null;
        int textBytes = 0;
        DownloadQueue? queue = SessionQueue ?? _source.MetadataQueue;
        for (int page = 0; page < 128; page++)
        {
            if (!pages.Add((request.Cursor, request.Page))) throw new PluginWorkerException("catalog_pagination_loop", "The source repeated a catalog continuation.");
            CatalogReadResult result;
            using (IDisposable? admission = queue == null ? null : await queue.AcquireMetadataAsync(_source.Name,
                DownloadPriority.BoardDiscovery, token).ConfigureAwait(false))
                result = await _source.InvokeAsync((worker, cancellation) => worker.ReadCatalogAsync(request,
                    operation, cancellation), token).ConfigureAwait(false);
            PluginSourceImageBoard.Validate(SdkValidation.ValidateCatalogResult(request, result, _source.Description));
            token.ThrowIfCancellationRequested();
            if (result is not CatalogSnapshot observed) return result;
            SourceCatalogSnapshot snapshot = observed.Snapshot;
            if (page == 0) firstValidators = snapshot.Validators;
            foreach (SourceThreadReference thread in snapshot.Threads)
            {
                if (!identities.Add(thread.Id)) continue;
                textBytes = checked(textBytes + System.Text.Encoding.UTF8.GetByteCount(thread.OpeningText ?? "")
                    + System.Text.Encoding.UTF8.GetByteCount(thread.Title ?? "") + thread.Url.AbsoluteUri.Length);
                if (threads.Count >= SdkLimits.MaximumCollectionCount || textBytes > SdkLimits.MaximumMetadataBytes)
                    throw new PluginWorkerException("catalog_too_large", "The combined catalog exceeds the host's bounded observation size.");
                threads.Add(thread);
            }
            if (snapshot.NextCursor == null && snapshot.NextPage == null)
                return new CatalogSnapshot(snapshot with { Threads = threads.AsReadOnly(), Validators = firstValidators });
            request = new CatalogReadRequest(request.Board, snapshot.NextCursor, snapshot.NextPage);
        }
        throw new PluginWorkerException("catalog_page_limit", "The catalog exceeded 128 continuation pages.");
    }

    protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (_retryAt is DateTimeOffset due && due - Clock.GetUtcNow() > interval) interval = due - Clock.GetUtcNow();
        return Task.Delay(interval, Clock, cancellationToken);
    }
}
