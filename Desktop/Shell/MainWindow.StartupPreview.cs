using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private bool _startupPreviewShown;
    private bool _startupPreviewRetired;
    private StartupPreviewSnapshot? _historicSource;
    // Display rows for the normal Downloads table until the first authoritative frame replaces them.
    internal ObservableCollection<ThreadRow> HistoricThreads { get; } = new();
    private void OnStartupPreviewChanged() => _sessionRefresh.RequestImmediate();
    // Raised off the UI thread by the host; only the archive read gate depends on it.
    private void OnCatalogReadableChanged() => Dispatcher.UIThread.Post(() => { if (!_isClosing) UpdateInteractionState(); });

    private void RefreshStartupPreview()
    {
        StartupPreviewSnapshot? preview = _session.StartupPreview;
        DesktopSessionState state = _session.Projection.View.State;
        // Startup is one-shot. Generic later Ready row slices must never revive
        // the historical cache after the first authoritative frame was consumed.
        if (_restoreCompleted && !_sessionRowsApplying && _appliedSessionProjection?.View.State == DesktopSessionState.Ready)
            _startupPreviewRetired = true;
        bool show = !_startupPreviewRetired && !_isClosing && preview is { Rows.Count: > 0 }
            && (state == DesktopSessionState.Starting
                || (state == DesktopSessionState.Ready && (!_restoreCompleted || _sessionRowsApplying)));
        DataGrid grid = ThreadsDataGrid;
        Border panel = StartupPreviewPanel;
        panel.IsVisible = show;
        if (show)
        {
            StartupPreviewTitle.Text = Texts.T("shell.startupPreview");
            StartupPreviewDetails.Text = Texts.T("shell.startupPreviewDetails",
                preview!.CapturedAtUtc.ToLocalTime(), preview.ThreadCount, preview.BoardCount, preview.Rows.Count);
            if (!ReferenceEquals(_historicSource, preview))
            {
                _historicSource = preview;
                foreach (ThreadRow stale in HistoricThreads) stale.Dispose();
                HistoricThreads.Clear();
                int index = 0;
                foreach (StartupPreviewRow cached in preview.Rows.Where(row => !row.IsBoard))
                    HistoricThreads.Add(ThreadRow.CreateHistorical(cached, index++));
            }
            if (!ReferenceEquals(grid.ItemsSource, HistoricThreads)) grid.ItemsSource = HistoricThreads;
        }
        else if (_startupPreviewShown)
        {
            grid.ItemsSource = VisibleThreads;
            foreach (ThreadRow stale in HistoricThreads) stale.Dispose();
            HistoricThreads.Clear();
            _historicSource = null;
        }
        _startupPreviewShown = show;
    }
}
