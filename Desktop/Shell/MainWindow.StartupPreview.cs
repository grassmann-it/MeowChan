using System;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private bool _startupPreviewShown;
    private bool _startupPreviewRetired;
    private void OnStartupPreviewChanged() => _sessionRefresh.RequestImmediate();

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
        Border panel = this.FindControl<Border>("StartupPreviewPanel")!;
        panel.IsVisible = show;
        if (show)
        {
            this.FindControl<Grid>("WorkspaceBody")!.IsVisible = false;
            this.FindControl<TextBlock>("StartupPreviewTitle")!.Text = Texts.T("shell.startupPreview");
            this.FindControl<TextBlock>("StartupPreviewDetails")!.Text = Texts.T("shell.startupPreviewDetails",
                preview!.CapturedAtUtc.ToLocalTime(), preview.ThreadCount, preview.BoardCount, preview.Rows.Count);
            ListBox rows = this.FindControl<ListBox>("StartupPreviewRows")!;
            if (!ReferenceEquals(rows.ItemsSource, preview.Rows)) rows.ItemsSource = preview.Rows;
        }
        else if (_startupPreviewShown)
        {
            this.FindControl<ListBox>("StartupPreviewRows")!.ItemsSource = null;
            this.FindControl<Grid>("WorkspaceBody")!.IsVisible = _selectedPage == WorkspacePage.Downloads;
        }
        _startupPreviewShown = show;
    }
}
