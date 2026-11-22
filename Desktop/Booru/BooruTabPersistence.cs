using System;
using System.Threading;
using System.Threading.Tasks;
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
    private BooruSearchTabState _lastSnapshot;
    private Task<bool> _lastSave = Task.FromResult(true);
    private readonly bool _loadFailed;
    private bool _disposed;

    internal BooruTabPersistence(BooruWorkbenchView view, BooruSearchTabStore store, Config config)
    {
        _view = view;
        _store = store;
        _config = config;
        try
        {
            if (config.RestoreBooruSearchTabs && store.Read() is { } snapshot)
                view.RestoreSearchTabs(snapshot, config.BooruSavedTabs, config.BooruSavedResultsPerTab);
        }
        catch (Exception error) when (BooruSearchTabStore.IsStoreError(error))
        {
            _loadFailed = true;
            Log.Warning("Could not restore local Booru search tabs");
        }

        _observed = view.Model.Search.View;
        _lastSnapshot = Capture();
        _debounce = new QueryDebounce(Save, post: callback => view.Dispatcher.Post(callback));
        view.SearchTabs.Changed += Schedule;
        view.Model.Search.Changed += OnSearchChanged;
    }

    private BooruSearchTabState Capture() =>
        _view.SearchTabs.CaptureTargets(_config.BooruSavedTabs, _config.BooruSavedResultsPerTab);

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
        BooruSearchTabState snapshot = Capture();
        if (snapshot.HasSameContent(_lastSnapshot) && (!_lastSave.IsCompleted || _lastSave.IsCompletedSuccessfully && _lastSave.Result))
            return;
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
