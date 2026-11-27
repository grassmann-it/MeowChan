using System.Collections.Immutable;
using System.Linq;
using MeowChan.Booru;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruSearchModel
{
    private readonly BooruWorkspaceContent? _content;

    internal bool IsMediaAllowed(BooruPostView post) => BooruWorkspaceContent.Allows(_content?.Snapshot.Filter, post);

    private BooruSearchView FilterView(BooruSearchView view)
    {
        if (_content is null || view.Rows.IsEmpty) return view;
        BooruContentFilter filter = _content.Snapshot.Filter;
        ImmutableArray<BooruResultRowView> rows = view.Rows
            .Where(row => BooruWorkspaceContent.Allows(filter, row.Post)).ToImmutableArray();
        int removed = view.Rows.Length - rows.Length;
        if (removed == 0) return view;
        return view with
        {
            Rows = rows,
            HiddenCount = view.HiddenCount + removed
        };
    }

    private void OnContentChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _view = FilterView(_view);
        }
        RaiseChanged();
    }
}
