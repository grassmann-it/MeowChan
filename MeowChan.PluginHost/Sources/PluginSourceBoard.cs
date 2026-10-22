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
            if (_retryAt is DateTimeOffset due && due > DateTimeOffset.UtcNow)
                throw new PluginWorkerException("source_deferred", $"Source is waiting until {due:O}.");
            CatalogReadRequest request = new(new SourceBoardId(_source.Name, BoardCode), Validators: _validators, HasSnapshot: _lastCatalog != null);
            CatalogReadResult result;
            DownloadQueue? queue = SessionQueue ?? _source.MetadataQueue;
            using (IDisposable? admission = queue == null ? null
                : await queue.AcquireMetadataAsync(_source.Name, DownloadPriority.BoardDiscovery, cancellationToken).ConfigureAwait(false))
                result = await _source.InvokeAsync((worker, token) => worker.ReadCatalogAsync(request,
                    new PluginOperationContext(BoardUri.AbsoluteUri, WatchGeneration), token), cancellationToken).ConfigureAwait(false);
            PluginSourceImageBoard.Validate(SdkValidation.ValidateCatalogResult(request, result, _source.Description));
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
                            Title = reference.Title ?? $"Thread {id}",
                            CatalogOpeningText = reference.OpeningTextFormat == SourceTextFormat.Html
                                ? ArchiveText.PlainText(reference.OpeningText) : reference.OpeningText ?? string.Empty
                        });
                    }
                    _validators = snapshot.Snapshot.Validators;
                    _retryAt = null;
                    _lastCatalog = threads.AsReadOnly();
                    return _lastCatalog;
                case CatalogUnchanged unchanged:
                    _validators = unchanged.Validators;
                    return _lastCatalog!;
                case CatalogDeferred deferred:
                    _retryAt = deferred.RetryAt;
                    throw new PluginWorkerException(deferred.ReasonCode, $"Source is waiting until {deferred.RetryAt:O}.");
                case CatalogFailure failure:
                    _retryAt = failure.Failure.RetryAt;
                    throw new PluginWorkerException(failure.Failure.Code, failure.Failure.Message);
                default: throw new PluginWorkerException("invalid_source_result", "Unsupported catalog result.");
            }
        }
        finally { _readGate.Release(); }
    }

    protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (_retryAt is DateTimeOffset due && due - DateTimeOffset.UtcNow > interval) interval = due - DateTimeOffset.UtcNow;
        return Task.Delay(interval, cancellationToken);
    }
}
