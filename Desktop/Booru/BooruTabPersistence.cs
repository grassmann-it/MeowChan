using System;
using System.Threading;
using System.Threading.Tasks;
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
    private bool _inspected;
    private long _editRevision;
    private long _writeRevision;
    private Task<bool> _write = Task.FromResult(true);

    internal BooruTabPersistence(BooruWorkbenchView view, BooruSearchTabStore store, Config config)
    {
        _view = view;
        _store = store;
        _config = config;
        if (config.RestoreBooruSearchTabs && InspectExisting() is { } snapshot)
            view.RestoreSearchTabs(snapshot);

        _observed = view.Model.Search.View;
        _debounce = new QueryDebounce(Save);
        view.SearchTabs.Changed += Schedule;
        view.Model.Search.Changed += OnSearchChanged;
    }

    private BooruTabSnapshot? InspectExisting()
    {
        _inspected = true;
        return _store.Load(_view.Model.Search.View, _config.BooruSavedResultsPerTab, _view.Model.Services.Source);
    }

    private void OnSearchChanged()
    {
        BooruSearchView current = _view.Model.Search.View;
        // The presentation clock raises Changed every second without replacing the view.
        if (!ReferenceEquals(Interlocked.Exchange(ref _observed, current), current)) Schedule();
    }

    private void Schedule()
    {
        Interlocked.Increment(ref _editRevision);
        _debounce.Request(SaveDelay);
    }

    private void Save()
    {
        if (!_config.RestoreBooruSearchTabs) return;
        long revision = Interlocked.Read(ref _editRevision);
        if (revision == _writeRevision && _write.IsCompletedSuccessfully && _write.Result) return;
        // On opt-in, check ownership of the file without replacing the live session with the old one.
        if (!_inspected) InspectExisting();
        _store.Save(_view.SearchTabs.Capture(_config.BooruSavedTabs), _config.BooruSavedResultsPerTab);
        _write = _store.PendingSave;
        _writeRevision = revision;
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
