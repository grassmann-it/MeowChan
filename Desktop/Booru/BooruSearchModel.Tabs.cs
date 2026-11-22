using System.Collections.Immutable;
using System.Linq;

namespace MeowChan.Desktop.Booru;

// Search-tab support of the search model: a tab keeps the immutable BooruSearchView it showed last, and switching puts that view back
// WITHOUT sending a request (a restored page is the page as it was; "Search again" stays the explicit way to repeat it).
internal sealed partial class BooruSearchModel
{
    private bool IsBound(BooruSearchView view) => IsBound(view.ProviderId, view.InstanceId);

    private bool IsBound(string sourceId, string instanceId) => sourceId == _search.ProviderId && instanceId == _search.InstanceId;

    private static bool MatchesView(BooruPostView post, BooruSearchView view) =>
        post.SourceId == view.ProviderId && post.Identity.InstanceId == view.InstanceId;

    internal BooruSearchView ViewFromTarget(BooruSearchTabTarget target)
    {
        bool bound = IsBound(target.Source, target.Instance);
        BooruInputMode mode = target.PostId is null ? BooruInputMode.Tags : BooruInputMode.Post;
        string input = target.PostId ?? target.Query ?? string.Empty;
        return new BooruSearchView(target.Source, bound ? _search.ProviderName : target.Source, mode, input,
            BooruInput.Parse(mode, input), bound ? BooruSearchStatus.Idle : BooruSearchStatus.Unavailable,
            target.Query, target.PostId, target.Page, true, ImmutableArray<BooruResultRowView>.Empty,
            null, bound ? null : BooruCodes.SourceUnavailable, null, InstanceId: target.Instance);
    }

    /// <summary>
    /// Shows <paramref name="snapshot"/> (null: a blank search) as the current state. A request that is still running is cancelled
    /// (the tab left behind shows "search again"), a "handing over" row becomes idle again (its answer would land in the wrong tab) and
    /// the local state of the restored rows is read again, which asks the queue only, never the provider.
    /// </summary>
    internal void Restore(BooruSearchView? snapshot, int tabId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _activeSearchTabId = tabId;
            BooruSearchView next = snapshot ?? new BooruSearchView(_search.ProviderId, _search.ProviderName, BooruInputMode.Tags,
                string.Empty, new BooruParsedInput(BooruInputKind.Empty), BooruSearchStatus.Idle, null, null, 1, true,
                ImmutableArray<BooruResultRowView>.Empty, null, null, null, InstanceId: _search.InstanceId);
            bool bound = IsBound(next);
            ImmutableArray<BooruResultRowView> rows = (bound ? next.Rows : ImmutableArray<BooruResultRowView>.Empty)
                .Where(row => MatchesView(row.Post, next))
                .Select(row =>
                {
                    if (row.Load == BooruRowLoad.Handing) row = row with { Load = BooruRowLoad.Idle, LoadCode = null };
                    if (row.Preview == BooruPreviewState.Loading) row = row with { Preview = BooruPreviewState.Unavailable };
                    return row;
                })
                .ToImmutableArray();
            // A search that was still running when the tab was left has no rows; it is offered again, never resumed.
            BooruSearchStatus status = bound ? (next.IsBusy ? BooruSearchStatus.Idle : next.Status) : BooruSearchStatus.Unavailable;
            _view = next with { Rows = rows, Status = status, Code = bound ? next.Code : BooruCodes.SourceUnavailable,
                Notice = null, Restored = true };
            _originalActions.Clear();
            _originalActionVersions.Clear();
            ReplaceRequest(needsRequest: !rows.IsEmpty);
        }
        RaiseChanged();
        OnLocalStatesChanged();
    }
}
