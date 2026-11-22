using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Everything one Danbooru window needs: the search tab, the watcher list, and the single action that
/// joins them (a watcher created from the search that is shown in the field).
/// </summary>
internal sealed class BooruWorkbenchModel : IDisposable
{
    private readonly Dictionary<(string Source, string Instance), BooruWatcherListModel> _watchers;

    internal BooruWorkbenchModel(BooruWorkbenchServices services, IReadOnlyList<BooruWorkbenchServices>? sources = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Sources = sources ?? [services];
        Search = new BooruSearchModel(Sources.Select(source => new BooruSearchModel.Binding(
            source.Search, source.Queue, source.Previews, source.Source)).ToArray(), services.Clock);
        _watchers = Sources.ToDictionary(source => (source.Search.ProviderId, source.Search.InstanceId),
            source => new BooruWatcherListModel(source.Watchers, source.Clock));
        foreach (BooruWatcherListModel watchers in _watchers.Values) watchers.Changed += OnWatchersChanged;
    }

    internal BooruWorkbenchServices Services { get; }
    internal IReadOnlyList<BooruWorkbenchServices> Sources { get; }
    internal BooruSearchModel Search { get; }
    internal BooruWatcherListModel Watchers => WatchersFor(Search.View);
    internal event Action? WatchersChanged;

    private void OnWatchersChanged() => WatchersChanged?.Invoke();

    private BooruWatcherListModel WatchersFor(BooruSearchView view) =>
        _watchers.GetValueOrDefault((view.ProviderId, view.InstanceId))
            ?? _watchers[(Services.Search.ProviderId, Services.Search.InstanceId)];

    /// <summary>Reads the stored watcher lists locally on first show.</summary>
    internal Task InitializeAsync() => Task.WhenAll(_watchers.Values.Select(watchers => watchers.RefreshAsync()));

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
        BooruWatcherListModel watchers = WatchersFor(view);
        if (watchers.HasWatcherFor(query))
        {
            Search.SetNotice(new BooruNotice(BooruNoticeKind.Info, "booru.ui.watcher.duplicate", new object?[] { query }));
            return false;
        }
        BooruWatcherCommandResult result = await watchers.CreateAsync(query).ConfigureAwait(false);
        BooruSearchView current = Search.View;
        if (current.ProviderId != view.ProviderId || current.InstanceId != view.InstanceId)
            return result.Status == BooruWatcherCommandStatus.Done;
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
        foreach (BooruWatcherListModel watchers in _watchers.Values)
        {
            watchers.Changed -= OnWatchersChanged;
            watchers.Dispose();
        }
    }
}
