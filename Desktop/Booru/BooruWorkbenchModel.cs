using System;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Everything one Danbooru window needs: the search tab, the watcher list, and the single action that
/// joins them (a watcher created from the search that is shown in the field).
/// </summary>
internal sealed class BooruWorkbenchModel : IDisposable
{
    internal BooruWorkbenchModel(BooruWorkbenchServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Search = new BooruSearchModel(services.Search, services.Queue, services.Previews, services.Clock);
        Watchers = new BooruWatcherListModel(services.Watchers, services.Clock);
    }

    internal BooruWorkbenchServices Services { get; }
    internal BooruSearchModel Search { get; }
    internal BooruWatcherListModel Watchers { get; }

    /// <summary>Only the read of the stored watcher list; nothing is requested from the provider.</summary>
    internal Task InitializeAsync() => Watchers.RefreshAsync();

    /// <summary>
    /// Stores a watcher for the exact query shown in the field. The watcher starts from now (the first
    /// check only sets the starting point), so creating it neither downloads existing posts nor starts a
    /// check. A one-shot post can never become a watcher, and the same search is not watched twice.
    /// </summary>
    internal async Task<bool> CreateWatcherAsync()
    {
        BooruSearchView view = Search.View;
        if (!Search.CanCreateWatcher || view.Parsed.Canonical is not { } query)
        {
            Search.SetNotice(new BooruNotice(BooruNoticeKind.Warning, "booru.ui.watcher.createNeedsTags"));
            return false;
        }
        if (Watchers.HasWatcherFor(query))
        {
            Search.SetNotice(new BooruNotice(BooruNoticeKind.Info, "booru.ui.watcher.duplicate", new object?[] { query }));
            return false;
        }
        BooruWatcherCommandResult result = await Watchers.CreateAsync(query).ConfigureAwait(false);
        if (result.Status == BooruWatcherCommandStatus.Done)
        {
            Search.SetNotice(new BooruNotice(BooruNoticeKind.Success, "booru.ui.watcher.created", new object?[] { query }));
            return true;
        }
        Search.SetNotice(new BooruNotice(BooruNoticeKind.Error, "booru.ui.watcher.createFailed",
            new object?[] { result.Code ?? result.Status.ToString() }));
        return false;
    }

    public void Dispose()
    {
        Search.Dispose();
        Watchers.Dispose();
    }
}
