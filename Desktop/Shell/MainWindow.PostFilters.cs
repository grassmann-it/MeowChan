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
        _postFilterSession = new PostFilterSession(_session.PostFilterService);
        ArchivePage.PostFilters = _postFilterSession;
        _session.StateChanged += OnPostFilterSessionStateChanged;
        Closed += (_, _) => _session.StateChanged -= OnPostFilterSessionStateChanged;
        LoadPostFiltersWhenReady();
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
