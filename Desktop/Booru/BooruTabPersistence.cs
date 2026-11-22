using System;
using System.Threading;
using MeowChan.Desktop.Controls;

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
    private bool _disposed;

    internal BooruTabPersistence(BooruWorkbenchView view, BooruSearchTabStore store, Config config)
    {
        _view = view;
        _store = store;
        _config = config;
        if (config.RestoreBooruSearchTabs
            && store.Load(view.Model.Search.View, TabLimit, config.BooruSavedResultsPerTab, view.Model.Services.Source) is { } snapshot)
            view.RestoreSearchTabs(snapshot);

        _observed = view.Model.Search.View;
        _debounce = new QueryDebounce(Save);
        view.SearchTabs.Changed += Schedule;
        view.Model.Search.Changed += OnSearchChanged;
    }

    private int TabLimit => Math.Min(_config.BooruSavedTabs, _view.SearchTabs.Limit + 1);

    private void OnSearchChanged()
    {
        BooruSearchView current = _view.Model.Search.View;
        // The presentation clock raises Changed every second without replacing the view.
        if (!ReferenceEquals(Interlocked.Exchange(ref _observed, current), current)) Schedule();
    }

    private void Schedule() => _debounce.Request(SaveDelay);

    private void Save()
    {
        if (_config.RestoreBooruSearchTabs)
            _store.Save(_view.SearchTabs.Capture(TabLimit), _config.BooruSavedResultsPerTab);
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
