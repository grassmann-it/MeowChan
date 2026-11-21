using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace MeowChan.Desktop.Booru;

// Search-tab support of the search model: a tab keeps the immutable BooruSearchView it showed last, and switching puts that view back
// WITHOUT sending a request (a restored page is the page as it was; "Search again" stays the explicit way to repeat it).
internal sealed partial class BooruSearchModel
{
    /// <summary>
    /// Shows <paramref name="snapshot"/> (null: a blank search) as the current state. A request that is still running is cancelled
    /// (the tab left behind shows "search again"), a "handing over" row becomes idle again (its answer would land in the wrong tab) and
    /// the local state of the restored rows is read again, which asks the queue only, never the provider.
    /// </summary>
    internal void Restore(BooruSearchView? snapshot)
    {
        bool follow;
        long id;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return;
            _request?.Cancel();
            BooruSearchView next = snapshot ?? new BooruSearchView(_search.ProviderId, _search.ProviderName, BooruInputMode.Tags,
                string.Empty, new BooruParsedInput(BooruInputKind.Empty), BooruSearchStatus.Idle, null, null, 1, true,
                ImmutableArray<BooruResultRowView>.Empty, null, null, null);
            ImmutableArray<BooruResultRowView> rows = next.Rows
                .Select(row => row.Load == BooruRowLoad.Handing ? row with { Load = BooruRowLoad.Idle, LoadCode = null } : row)
                .ToImmutableArray();
            // A search that was still running when the tab was left has no rows; it is offered again, never resumed.
            BooruSearchStatus status = next.Status == BooruSearchStatus.Searching ? BooruSearchStatus.Idle : next.Status;
            _view = next with { Rows = rows, Status = status, Notice = null };
            _originalActions.Clear();
            _originalActionVersions.Clear();
            _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            id = ++_requestId;
            token = _request.Token;
            follow = !rows.IsEmpty;
        }
        RaiseChanged();
        if (follow) StartFollowUps(id, token);
    }
}
