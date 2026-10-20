using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private readonly HashSet<long> _mediaRetryCommands = new();

    private async void OnRetryMissingMediaClick(object? sender, RoutedEventArgs e)
    {
        if (!_restoreCompleted || WorkspaceCommandBusy || GetWorkspaceSelection() is not ThreadRow row
            || !row.CanRetryMedia || !_mediaRetryCommands.Add(row.WatchId)) return;
        RefreshMediaRetryControls();
        try
        {
            int accepted = await _session.RetryMissingMediaAsync(row.WatchId);
            ShowNotification(Texts.T("notification.session"), Texts.T(accepted == 0 ? "work.retryNone"
                : _session.IsPaused ? "work.retryPaused" : "work.retryAccepted", accepted),
                accepted == 0 ? NotificationType.Warning : NotificationType.Information);
        }
        catch (Exception exception)
        {
            ShowNotification(Texts.T("notification.error"), Texts.T("work.retryFailed", exception.Message), NotificationType.Error);
        }
        finally
        {
            _mediaRetryCommands.Remove(row.WatchId);
            RefreshWorkspace();
        }
    }

    private void RefreshMediaRetryControls()
    {
        bool enabled = _restoreCompleted && !WorkspaceCommandBusy && GetWorkspaceSelection() is ThreadRow row
            && row.CanRetryMedia && !_mediaRetryCommands.Contains(row.WatchId);
        if (this.FindControl<Button>("InspectorMediaRetryButton") is Button button) button.IsEnabled = enabled;
        if (this.FindControl<MenuItem>("ThreadMediaRetryMenuItem") is MenuItem item) item.IsEnabled = enabled;
    }
}
