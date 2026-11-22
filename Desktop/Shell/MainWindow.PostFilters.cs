using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

// Post filters (plan P2): one shared snapshot for the reader and its dialog, loaded once the session is ready.
public partial class MainWindow
{
    private PostFilterSession? _postFilterSession;
    private bool _postFiltersLoading;

    private void InitializePostFilters()
    {
        PostFilterSession filters = new(_session.PostFilterService);
        _postFilterSession = filters;
        ArchivePage.PostFilters = filters;
        filters.Changed += OnPostFiltersChanged;
        _session.StateChanged += OnPostFilterSessionStateChanged;
        Closed += (_, _) =>
        {
            _session.StateChanged -= OnPostFilterSessionStateChanged;
            filters.Changed -= OnPostFiltersChanged;
        };
        LoadPostFiltersWhenReady();
    }

    private void OnPostFiltersChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshSettingsPostFilters();
        else Dispatcher.UIThread.Post(RefreshSettingsPostFilters);
    }

    private void RefreshSettingsPostFilters()
    {
        if (!_isClosing) _settingsEditor?.UpdatePostFilterRules(_postFilterSession?.CurrentForSettings);
    }

    private void OnPostFilterSessionStateChanged() => Dispatcher.UIThread.Post(LoadPostFiltersWhenReady);

    private void LoadPostFiltersWhenReady()
    {
        if (_isClosing || _postFiltersLoading || _postFilterSession is not { Current: null } filters
            || _session.State != DesktopSessionState.Ready) return;
        _postFiltersLoading = true;
        _ = LoadPostFiltersAsync(filters);
    }

    private async Task LoadPostFiltersAsync(PostFilterSession filters)
    {
        // A failure keeps the reader unfiltered; the dialog shows the reason and loads again when it opens.
        try { await filters.LoadAsync(); }
        finally { _postFiltersLoading = false; }
    }
}
