using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Desktop.Controls;
using Serilog;

namespace MeowChan.Desktop.Booru;

/// <summary>Tracks one page's tab edits and snapshots them on the workspace dispatcher.</summary>
internal sealed class BooruTabPersistence : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly BooruWorkbenchView _view;
    private readonly BooruSearchTabStore _store;
    private readonly Config _config;
    private readonly QueryDebounce _debounce;
    private BooruSearchView _observed;
    private BooruSearchTabState _observedSnapshot;
    private BooruSearchTabState _lastSnapshot;
    private Task<bool> _lastSave = Task.FromResult(true);
    private bool _inspected;
    private bool _loadFailed;
    private bool _disposed;

    internal BooruTabPersistence(BooruWorkbenchView view, BooruSearchTabStore store, Config config)
    {
        _view = view;
        _store = store;
        _config = config;
        if (config.RestoreBooruSearchTabs && InspectExisting(migrateLegacy: true) is { } snapshot)
            view.RestoreSearchTabs(snapshot, config.BooruSavedTabs, config.BooruSavedResultsPerTab);

        _observed = view.Model.Search.View;
        _lastSnapshot = _observedSnapshot = Capture(view.Model.Search.ReadContentFilter(), Config.BooruSavedTabsMaximum);
        _debounce = new QueryDebounce(Save, post: callback => view.Dispatcher.Post(callback));
        view.SearchTabs.Changed += Schedule;
        view.Model.Search.Changed += OnSearchChanged;
    }

    private BooruSearchTabState Capture(BooruContentFilter? filter, int tabLimit) =>
        _view.SearchTabs.CaptureTargets(filter, tabLimit, _config.BooruSavedResultsPerTab);

    private BooruSearchTabState? InspectExisting(bool migrateLegacy)
    {
        _inspected = true;
        try { return _store.Read(migrateLegacy); }
        catch (Exception error) when (BooruSearchTabStore.IsStoreError(error))
        {
            _loadFailed = true;
            Log.Warning("Could not restore local Booru search tabs");
            return null;
        }
    }

    private void OnSearchChanged()
    {
        BooruSearchView current = _view.Model.Search.View;
        // The presentation clock raises Changed every second without replacing the view.
        if (!ReferenceEquals(Interlocked.Exchange(ref _observed, current), current)) Schedule();
    }

    private void Schedule() => _debounce.Request(SaveDelay);

    private void Save()
    {
        if (_loadFailed || !_config.RestoreBooruSearchTabs) return;
        BooruContentFilter? filter = _view.Model.Search.ReadContentFilter();
        BooruSearchTabState current = Capture(filter, Config.BooruSavedTabsMaximum);
        // Tab switches and content rule changes must keep previously saved drafts.
        BooruSearchTabState previous = BooruSearchTabs.FilterCapturedRows(_observedSnapshot, filter);
        int tabLimit = current.HasSameTabs(previous)
            ? Math.Max(_config.BooruSavedTabs, _lastSnapshot.Tabs.Length)
            : _config.BooruSavedTabs;
        _observedSnapshot = current;
        BooruSearchTabState snapshot = current.Tabs.Length <= tabLimit ? current : Capture(filter, tabLimit);
        if (snapshot.HasSameContent(_lastSnapshot) && (!_lastSave.IsCompleted || _lastSave.IsCompletedSuccessfully && _lastSave.Result))
            return;
        // On opt-in, inspect the previous file while keeping the current workbench session.
        if (!_inspected) _ = InspectExisting(migrateLegacy: false);
        if (_loadFailed) return;
        _lastSnapshot = snapshot;
        _lastSave = _store.Save(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.SearchTabs.Changed -= Schedule;
        _view.Model.Search.Changed -= OnSearchChanged;
        _debounce.Dispose();
        Save();
    }
}
