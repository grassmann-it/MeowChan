using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Booru;

internal enum BooruBulkPhase { Idle, Running, Finished }

/// <summary>Why the listing of "Download all of this search" stopped. Never "complete": the source only ever lists what its page limit allows.</summary>
internal enum BooruBulkEnd
{
    None,
    /// <summary>The source's page limit (5 pages of 20 on Danbooru) was reached; the search may have more results.</summary>
    PageLimit,
    /// <summary>The source reported that there is nothing after the last page it returned.</summary>
    SourceEnd,
    /// <summary>The source stopped answering (pause, rate limit, offline, denied, error); the rest was not listed.</summary>
    Blocked,
    Canceled
}

/// <summary>What "Download all of this search" found and queued so far. Counts are posts of the search, de-duplicated by post id.</summary>
internal sealed record BooruBulkView(BooruBulkPhase Phase, string? Query = null, int PagesRead = 0, int Found = 0, int Queued = 0,
    int AlreadyQueued = 0, int AlreadySaved = 0, int NoOriginal = 0, int Failed = 0, BooruBulkEnd End = BooruBulkEnd.None)
{
    internal int TabId { get; init; }
    internal bool IsRunning => Phase == BooruBulkPhase.Running;
}

// "Download all of this search": the existing results of the shown search, bounded by the source's real page limit, handed to the same
// queue port as the single "Load original" button (so provenance, de-duplication and the Downloads integration are the existing ones).
// The watcher only takes posts that are new after its creation; this is the explicit one-time backfill.
internal sealed partial class BooruSearchModel
{
    private int _activeSearchTabId = BooruSearchTabs.PrimaryId;
    private BooruBulkView _bulk = new(BooruBulkPhase.Idle);
    // The cancel action of the running collection (its token source is a local of DownloadAllAsync, so this class owns no extra disposable field).
    private Action? _cancelBulk;

    internal BooruBulkView Bulk { get { lock (_gate) return _bulk; } }

    /// <summary>True for a shown tag search (not a one-shot post) that is not busy, not paused and not already being collected.</summary>
    internal bool CanDownloadAll
    {
        get
        {
            lock (_gate) return CanDownloadAllLocked();
        }
    }

    private bool CanDownloadAllLocked() => !_disposed && !_bulk.IsRunning && _view.ExecutedQuery is not null && !_view.IsPostResult
        && _view.Status is BooruSearchStatus.Results or BooruSearchStatus.Empty && PauseEnd(_view) is null;

    /// <summary>Stops the running collection; what was queued before stays queued.</summary>
    internal void CancelBulk(int? tabId = null)
    {
        lock (_gate)
            if (_bulk.TabId == (tabId ?? _activeSearchTabId)) _cancelBulk?.Invoke();
    }

    internal async Task DownloadAllAsync()
    {
        using RequestLifetime? lifetime = AcquireModelLifetime();
        if (lifetime is null) return;
        using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        string query;
        int tabId;
        lock (_gate)
        {
            if (!CanDownloadAllLocked()) return;
            query = _view.ExecutedQuery!;
            tabId = _activeSearchTabId;
            _cancelBulk = source.Cancel;
            _bulk = new BooruBulkView(BooruBulkPhase.Running, query) { TabId = tabId };
        }
        RaiseChanged();
        BooruBulkView state;
        BooruBulkEnd end;
        try { (state, end) = await CollectAsync(query, tabId, source.Token).ConfigureAwait(false); }
        finally
        {
            lock (_gate) _cancelBulk = null;
        }
        lock (_gate)
        {
            if (_disposed) return;
            _bulk = state with { Phase = BooruBulkPhase.Finished, End = end == BooruBulkEnd.None ? BooruBulkEnd.Blocked : end };
        }
        RaiseChanged();
    }

    private async Task<(BooruBulkView State, BooruBulkEnd End)> CollectAsync(string query, int tabId, CancellationToken token)
    {
        BooruBulkView state = new(BooruBulkPhase.Running, query) { TabId = tabId };
        BooruBulkEnd end = BooruBulkEnd.None;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int page = 1; page <= MaximumPage && end == BooruBulkEnd.None; page++)
        {
            BooruCallResult<BooruResultPage> result;
            try { result = await _search.SearchAsync(query, page, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { end = BooruBulkEnd.Canceled; break; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Failed, "exception");
            }
            if (result.Status != BooruCallStatus.Ok || result.Value is not { } value || value.Page != page
                || !string.Equals(value.Query, query, StringComparison.Ordinal) || value.Posts.Length > PageSize)
            {
                end = BooruBulkEnd.Blocked;
                break;
            }
            state = state with { PagesRead = page };
            foreach (BooruPostView post in value.Posts)
            {
                if (!seen.Add(post.PostId)) continue;
                state = state with { Found = state.Found + 1 };
                if (post.OriginalAccess != BooruOriginalAccess.Available)
                {
                    state = state with { NoOriginal = state.NoOriginal + 1 };
                    Publish(state);
                    continue;
                }
                BooruLoadResult load;
                try { load = await _queue.LoadOriginalAsync(post, BooruLoadOrigin.Search, query, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { end = BooruBulkEnd.Canceled; break; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    load = new BooruLoadResult(BooruLoadStatus.Failed, "exception");
                }
                state = load.Status switch
                {
                    BooruLoadStatus.Queued => state with { Queued = state.Queued + 1 },
                    BooruLoadStatus.AlreadyQueued => state with { AlreadyQueued = state.AlreadyQueued + 1 },
                    BooruLoadStatus.AlreadyLoaded => state with { AlreadySaved = state.AlreadySaved + 1 },
                    BooruLoadStatus.NoOriginal or BooruLoadStatus.Restricted => state with { NoOriginal = state.NoOriginal + 1 },
                    _ => state with { Failed = state.Failed + 1 }
                };
                Publish(state);
            }
            if (end != BooruBulkEnd.None) break;
            if (value.IsEnd || value.Posts.IsEmpty) end = BooruBulkEnd.SourceEnd;
            else if (page >= MaximumPage) end = BooruBulkEnd.PageLimit;
        }
        return (state, end);
    }

    private void Publish(BooruBulkView state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _bulk = state;
        }
        RaiseChanged();
    }
}
