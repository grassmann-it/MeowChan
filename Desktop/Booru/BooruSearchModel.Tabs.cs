using System.Collections.Immutable;
using MeowChan.Booru;
using System.IO;
using System.Linq;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

// Search-tab support of the search model: a tab keeps the immutable BooruSearchView it showed last, and switching puts that view back
// WITHOUT sending a request (a restored page is the page as it was; "Search again" stays the explicit way to repeat it).
internal sealed partial class BooruSearchModel
{
    private bool IsBound(BooruSearchView view) => BindingFor(view) is not null;

    private Binding? BindingFor(BooruSearchView view) => BindingFor(view.ProviderId, view.InstanceId);

    private Binding? BindingFor(string sourceId, string instanceId) =>
        _bindings.FirstOrDefault(binding => binding.Search.ProviderId == sourceId && binding.Search.InstanceId == instanceId);

    private static BooruSearchView BlankView(Binding binding) =>
        new(binding.Search.ProviderId, binding.Search.ProviderName, BooruInputMode.Tags, string.Empty,
            new BooruParsedInput(BooruInputKind.Empty), BooruSearchStatus.Idle, null, null, 1, true,
            ImmutableArray<BooruResultRowView>.Empty, null, null, null, InstanceId: binding.Search.InstanceId);

    internal BooruSearchView? NewSourceView(string sourceId, string instanceId, BooruSearchView? draft = null)
    {
        if (BindingFor(sourceId, instanceId) is not { } binding) return null;
        BooruSearchView view = BlankView(binding);
        return draft is null ? view : view with
        {
            Mode = draft.Mode, Input = draft.Input, Parsed = ParseInput(draft.Mode, draft.Input, sourceId)
        };
    }

    private static BooruParsedInput ParseInput(BooruInputMode mode, string input, string sourceId) =>
        BooruInput.Parse(mode, input, sourceId);

    private static bool MatchesView(BooruPostView post, BooruSearchView view) =>
        post.SourceId == view.ProviderId && post.Identity.InstanceId == view.InstanceId;

    internal BooruSearchView ViewFromTarget(BooruSearchTabTarget target, int resultLimit, SourceDescription? source)
    {
        Binding? binding = BindingFor(target.Source, target.Instance);
        bool bound = binding is not null;
        source = binding?.Source ?? (source?.SourceId == target.Source ? source : null);
        BooruInputMode mode = target.Mode ?? (target.PostId is null ? BooruInputMode.Tags : BooruInputMode.Post);
        string input = target.Input ?? target.PostId ?? target.Query ?? string.Empty;
        ImmutableArray<BooruResultRowView> rows = bound && target.Rows is { } cached
            ? cached.Take(resultLimit).Where(row => IsValidCachedPost(row.Post, target, source)).Select(row =>
            {
                BooruPostView post = BooruPostView.From(row.Post, target.Instance);
                BooruPreviewState preview = row.Preview is not null ? BooruPreviewState.Ready
                    : post.PreviewUrl is not null ? BooruPreviewState.Unavailable : BooruPreviewState.None;
                return new BooruResultRowView(post, Preview: preview, PreviewBytes: row.Preview);
            }).ToImmutableArray()
            : ImmutableArray<BooruResultRowView>.Empty;
        BooruSearchStatus status = !bound ? BooruSearchStatus.Unavailable
            : !rows.IsEmpty ? BooruSearchStatus.Results : target.Empty ? BooruSearchStatus.Empty : BooruSearchStatus.Idle;
        return new BooruSearchView(target.Source, binding?.Search.ProviderName ?? target.Source, mode, input,
            ParseInput(mode, input, target.Source), status, target.Query, target.PostId, target.Page, target.IsEnd, rows,
            null, bound ? null : BooruCodes.SourceUnavailable, null, InstanceId: target.Instance,
            NeedsLoad: status == BooruSearchStatus.Idle
                && (!string.IsNullOrWhiteSpace(input) || target.Query is not null || target.PostId is not null))
        { PagerLimits = new(target.MaximumPage, target.PostsPerPage) };
    }

    private static bool IsValidCachedPost(BooruPost post, BooruSearchTabTarget target, SourceDescription? source)
    {
        if (post.Id.SourceId != target.Source) return false;
        if (source is not null)
            return SdkValidation.ValidateBooruPostResult(new BooruPostRequest(post.Id), new BooruPostSnapshot(post), source).Count == 0;
        try
        {
            // The SDK allows signed scores, while the archive claim mapper requires nonnegative counters.
            _ = BooruClaimMapper.Map(post with { Score = null }, target.Source, target.Instance);
            return true;
        }
        catch (InvalidDataException) { return false; }
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
            BooruSearchView next = snapshot ?? BlankView(_defaultBinding);
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
