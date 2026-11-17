using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Booru;

/// <summary>Queue admission counts, NOT completed transfers. Pending includes the current handoff.</summary>
internal sealed record BooruBatchView(bool Running = false, bool CancelRequested = false,
    int Succeeded = 0, int Failed = 0, int Pending = 0, bool Started = false);

internal sealed partial class BooruSearchModel
{
    // Page-scoped, deliberately smaller than a full provider page. No implicit select-all.
    internal const int MaximumSelection = 10;
    private ImmutableHashSet<string> _selected = ImmutableHashSet<string>.Empty;
    private ImmutableArray<string> _batchRecovery = ImmutableArray<string>.Empty;
    private BooruBatchView _batch = new();

    internal ImmutableHashSet<string> Selected { get { lock (_gate) return _selected; } }
    internal BooruBatchView Batch { get { lock (_gate) return _batch; } }
    internal bool CanLoadSelected { get { lock (_gate) return !_disposed && !_batch.Running
        && !_view.IsBusy && !_view.Rows.Any(row => row.Load == BooruRowLoad.Handing) && !_selected.IsEmpty && PauseEnd(_view) is null; } }
    internal bool CanRetryBatch { get { lock (_gate) return !_disposed && !_batch.Running
        && !_view.IsBusy && !_view.Rows.Any(row => row.Load == BooruRowLoad.Handing) && !_batchRecovery.IsEmpty && PauseEnd(_view) is null; } }

    /// <summary>Only an explicit checkbox changes selection; previewing and opening a hit never do.</summary>
    internal void SetSelected(string postId, bool selected)
    {
        lock (_gate)
        {
            if (_disposed || _batch.Running || !_view.Rows.Any(row => row.Post.PostId == postId)) return;
            if (selected && !_selected.Contains(postId) && _selected.Count >= MaximumSelection)
                _view = _view with { Notice = new BooruNotice(BooruNoticeKind.Warning,
                    "booru.ui.selection.exceeded", new object?[] { MaximumSelection }) };
            else _selected = selected ? _selected.Add(postId) : _selected.Remove(postId);
        }
        RaiseChanged();
    }

    internal Task LoadSelectedAsync() => RunBatchAsync(retry: false);
    internal Task RetryBatchAsync() => RunBatchAsync(retry: true);

    /// <summary>Stop after the current atomic queue handoff. Never claim that accepted jobs were cancelled.</summary>
    internal void CancelBatch()
    {
        lock (_gate)
        {
            if (_disposed || !_batch.Running) return;
            _batch = _batch with { CancelRequested = true };
        }
        RaiseChanged();
    }

    private void ResetSelection()
    {
        _selected = ImmutableHashSet<string>.Empty;
        _batchRecovery = ImmutableArray<string>.Empty;
        _batch = new();
    }

    private async Task RunBatchAsync(bool retry)
    {
        ImmutableArray<string> ids;
        lock (_gate)
        {
            if (retry ? !CanRetryBatch : !CanLoadSelected) return;
            // Freeze only the explicitly selected ids, in visible order. A retry freezes only unresolved ids.
            ids = retry ? _batchRecovery : _view.Rows.Where(row => _selected.Contains(row.Post.PostId))
                .Select(row => row.Post.PostId).Distinct(StringComparer.Ordinal).ToImmutableArray();
            _batchRecovery = ids;
            _batch = new BooruBatchView(Running: true, Pending: ids.Length, Started: true);
        }
        RaiseChanged();
        ImmutableArray<string>.Builder unresolved = ImmutableArray.CreateBuilder<string>();
        int processed = 0;
        foreach (string id in ids)
        {
            BooruResultRowView row;
            BooruLoadOrigin origin;
            string? query;
            long version;
            lock (_gate)
            {
                if (_disposed) return;
                if (_batch.CancelRequested) break;
                row = _view.Rows.First(item => item.Post.PostId == id);
                origin = _view.IsPostResult ? BooruLoadOrigin.OneShot : BooruLoadOrigin.Search;
                query = origin == BooruLoadOrigin.Search ? _view.ExecutedQuery : null;
                version = BeginOriginalAction(id);
                int index = _view.Rows.IndexWhere(item => item.Post.PostId == id);
                SetRow(index, row with { Load = BooruRowLoad.Handing, LoadCode = null });
            }
            RaiseChanged();
            BooruLoadResult result;
            try
            {
                // Existing/in-flight work is success, never a second admission. Unavailable originals fail locally.
                result = row.Load is BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.Handing
                    ? new BooruLoadResult(BooruLoadStatus.AlreadyQueued)
                    : row.Load == BooruRowLoad.AlreadyLoaded ? new BooruLoadResult(BooruLoadStatus.AlreadyLoaded)
                    : row.Post.OriginalAccess != BooruOriginalAccess.Available
                        ? new BooruLoadResult(row.Post.OriginalAccess == BooruOriginalAccess.Restricted
                            ? BooruLoadStatus.Restricted : BooruLoadStatus.NoOriginal)
                    : row.CanRetry
                        ? await _queue.RetryOriginalAsync(id, _lifetime.Token).ConfigureAwait(false)
                        : await _queue.LoadOriginalAsync(row.Post, origin, query, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { result = new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.Canceled); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { result = new BooruLoadResult(BooruLoadStatus.Failed, "exception"); }
            bool succeeded = result.Status is BooruLoadStatus.Queued or BooruLoadStatus.AlreadyQueued or BooruLoadStatus.AlreadyLoaded;
            lock (_gate)
            {
                if (_disposed) return;
                int index = _view.Rows.IndexWhere(item => item.Post.PostId == id);
                if (index >= 0 && IsCurrentOriginalAction(id, version))
                    SetRow(index, _view.Rows[index] with { Load = MapLoad(result), LoadCode = result.Code });
                if (!succeeded) unresolved.Add(id);
                processed++;
                _batch = _batch with { Succeeded = _batch.Succeeded + (succeeded ? 1 : 0),
                    Failed = _batch.Failed + (succeeded ? 0 : 1), Pending = ids.Length - processed };
                // Independent cancellation is also an aborted operation: stop, retain the rest, require explicit recovery.
                if (result.Code is BooruCodes.Canceled or BooruCodes.Cancelled)
                    _batch = _batch with { CancelRequested = true };
            }
            RaiseChanged();
        }
        lock (_gate)
        {
            if (_disposed) return;
            unresolved.AddRange(ids.Skip(processed));
            _batchRecovery = unresolved.ToImmutable();
            _batch = _batch with { Running = false };
        }
        RaiseChanged();
    }
}
