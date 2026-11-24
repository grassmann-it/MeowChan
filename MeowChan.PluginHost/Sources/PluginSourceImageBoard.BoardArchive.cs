using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources;

internal sealed record BoardArchiveThread(string Id, string Url)
{
    public override string ToString() => "#" + Id;
}

internal sealed record BoardArchiveThreadDetails(string? Title, string OpeningPost, SourceThreadState State, bool AlreadyWatching = false);

public sealed partial class PluginSourceImageBoard
{
    private sealed record ArchiveSnapshot(IReadOnlyList<BoardArchiveThread> Threads, SourceValidators? Validators);

    // Keep a small session cache. Only an accepted response can become a conditional baseline.
    private const int ArchiveCacheSize = 8;
    private readonly Dictionary<string, ArchiveSnapshot> _archiveCache = new(StringComparer.Ordinal);
    private readonly object _archiveCacheLock = new();

    internal async Task<IReadOnlyList<BoardArchiveThread>?> ReadBoardArchiveAsync(Uri archiveUri, CancellationToken token)
    {
        if (!Router.TryResolve(archiveUri, out SourceResourceKind kind, out string board, out _, out Uri? canonical)
            || kind != SourceResourceKind.BoardArchive)
            throw new ArgumentException("A declared board archive URL is required.", nameof(archiveUri));

        using CancellationTokenSource lifetime = LinkLifetime(token);
        token = lifetime.Token;
        ArchiveSnapshot? previous;
        lock (_archiveCacheLock) _archiveCache.TryGetValue(board, out previous);
        var request = new CatalogReadRequest(new SourceBoardId(Name, board),
            Validators: previous?.Validators, HasSnapshot: previous != null) { IsArchive = true };
        using var history = new PluginHostHttp.MetadataHistory();
        CatalogReadResult result = await ReadArchiveMetadataAsync(canonical!.AbsoluteUri, history,
            (worker, operation, cancellation) => worker.ReadCatalogAsync(request, operation, cancellation), token).ConfigureAwait(false);
        Validate(SdkValidation.ValidateCatalogResult(request, result, Description));

        ArchiveSnapshot? next;
        switch (result)
        {
            case CatalogSnapshot { Snapshot: { IsComplete: true, NextPage: null, NextCursor: null } snapshot }:
                List<BoardArchiveThread> threads = new(snapshot.Threads.Count);
                foreach (SourceThreadReference reference in snapshot.Threads)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Router.TryResolve(reference.Url, out SourceResourceKind threadKind, out string threadBoard, out string? id, out Uri? url)
                        || threadKind != SourceResourceKind.Thread || threadBoard != board || id != reference.Id.ThreadId)
                        throw new PluginWorkerException("invalid_source_result", "Archive URL does not identify the declared thread.");
                    threads.Add(new(id, url!.AbsoluteUri));
                }
                next = new ArchiveSnapshot(threads.AsReadOnly(), snapshot.Validators);
                break;
            case CatalogUnchanged unchanged when previous != null:
                next = previous with { Validators = unchanged.Validators };
                break;
            case CatalogFailure { Failure: { Code: "archive_unavailable", HttpStatusCode: 404 } }:
                next = null;
                break;
            case CatalogFailure failure:
                throw PluginSourceDiagnostics.FinalFailure(history, failure.Failure);
            case CatalogDeferred deferred:
                throw ArchiveDeferred(history, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
            default:
                throw new PluginWorkerException("invalid_source_result", "The archive requires a complete snapshot.");
        }

        lock (_archiveCacheLock)
        {
            token.ThrowIfCancellationRequested();
            // A slower observation must not replace another accepted snapshot.
            if (ReferenceEquals(_archiveCache.GetValueOrDefault(board), previous))
            {
                if (next is null) _archiveCache.Remove(board);
                else
                {
                    if (previous == null && _archiveCache.Count == ArchiveCacheSize)
                        _archiveCache.Remove(_archiveCache.Keys.First());
                    _archiveCache[board] = next;
                }
            }
        }
        return next?.Threads;
    }

    internal async Task<BoardArchiveThreadDetails?> ReadBoardArchiveThreadAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !Router.TryResolve(uri, out SourceResourceKind kind, out string board, out string? id, out Uri? canonical)
            || kind != SourceResourceKind.Thread)
            throw new ArgumentException("A declared thread URL is required.", nameof(url));

        using CancellationTokenSource lifetime = LinkLifetime(token);
        token = lifetime.Token;
        var request = new ThreadReadRequest(new SourceThreadId(Name, board, id!));
        using var history = new PluginHostHttp.MetadataHistory();
        ThreadReadResult result = await ReadArchiveMetadataAsync(canonical!.AbsoluteUri, history,
            (worker, operation, cancellation) => worker.ReadThreadAsync(request, operation, cancellation), token).ConfigureAwait(false);
        Validate(SdkValidation.ValidateThreadResult(request, result, Description));
        switch (result)
        {
            case PluginSdk.ThreadSnapshot { Snapshot: var snapshot }:
                if (!Router.TryResolve(snapshot.Url, out SourceResourceKind threadKind, out string threadBoard, out string? threadId, out _)
                    || threadKind != SourceResourceKind.Thread || threadBoard != board || threadId != id)
                    throw new PluginWorkerException("invalid_source_result", "Snapshot URL does not identify the selected thread.");
                SourcePost? op = snapshot.Posts.FirstOrDefault(post => post.PostId == id);
                string openingPost = op is { TextFormat: SourceTextFormat.Html } ? ArchiveText.PlainText(op.Text) : op?.Text ?? string.Empty;
                return new BoardArchiveThreadDetails(snapshot.Title, openingPost, snapshot.SourceState);
            case ThreadNotFound:
                return null;
            case ThreadFailure failure:
                throw PluginSourceDiagnostics.FinalFailure(history, failure.Failure);
            case ThreadDeferred deferred:
                throw ArchiveDeferred(history, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
            default:
                throw new PluginWorkerException("invalid_source_result", "The preview requires a complete thread.");
        }
    }

    private async Task<T> ReadArchiveMetadataAsync<T>(string url, PluginHostHttp.MetadataHistory history,
        Func<IPluginWorkerClient, PluginOperationContext, CancellationToken, Task<T>> read, CancellationToken token)
    {
        var operation = new PluginOperationContext(url, 0);
        history.Bind(operation);
        history.BeginSourceResult();
        using IDisposable? admission = MetadataQueue == null ? null : await MetadataQueue.AcquireMetadataAsync(
            Name, DownloadPriority.Manual, token).ConfigureAwait(false);
        return await InvokeAsync((worker, cancellation) => read(worker, operation, cancellation), token).ConfigureAwait(false);
    }

    private static SourceCheckDeferredException ArchiveDeferred(PluginHostHttp.MetadataHistory history, DateTimeOffset retryAt,
        string reason, int? status) => new(retryAt, reason, history.InitialNoSend,
            PluginSourceDiagnostics.DeferredFailure(history, reason, status, retryAt));
}
