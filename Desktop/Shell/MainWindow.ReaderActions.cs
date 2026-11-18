using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private void SetupInspectorLocalActions()
    {
        CommandBindings.Apply(InspectorReadPosts, "thread.read", InspectorReadReason, iconWithLabel: true);
        CommandBindings.Apply(InspectorGallery, "gallery.open", iconWithLabel: true);
        CommandBindings.Apply(InspectorHtmlExport, "export.open-html-dialog", iconWithLabel: true);
    }

    private ThreadRow? LeadRowOf(DownloadMenuTarget frozen) => _threadRows.TryGetValue(frozen.LeadWatch, out ThreadRow? row) ? row : null;

    // DL-01: a row whose thread was never captured has no reader (see DownloadReaderGate); the menu entry and the button are disabled with this
    // reason, and the keys and the double click that reach the open path anyway say it as a message instead of opening an empty reader.
    private static bool NothingCaptured(ThreadRow? row) => row != null && DownloadReaderGate.NothingCaptured(row.Snapshot);

    private string? InspectorReadReason() =>
        SelectedThreads().Length == 1 && NothingCaptured(GetWorkspaceSelection() as ThreadRow) ? Texts.T("menu.unavailable.nothingCaptured") : null;

    private bool ReportNothingCaptured(ThreadRow? row)
    {
        if (!NothingCaptured(row)) return false;
        ShowNotification(Texts.T("notification.session"), Texts.T("menu.unavailable.nothingCaptured"), NotificationType.Information,
            context: row!.WatchId.ToString(), operation: "downloads.read.nothingCaptured");
        return true;
    }

    private void RefreshInspectorLocalActions()
    {
        bool thread = GetWorkspaceSelection() is Desktop.ThreadRow;
        InspectorLocalActions.IsVisible = thread;
        bool ready = thread && _restoreCompleted && !WorkspaceCommandBusy && !_selectionCommandRunning;
        string? uncaptured = ready ? InspectorReadReason() : null;
        InspectorReadPosts.IsEnabled = ready && SelectedThreads().Length == 1 && uncaptured == null;
        // The reason stands in the tip of the disabled button; the command's own tip returns with the next capture.
        string readTip = CommandBindings.Tooltip("thread.read", uncaptured);
        ToolTip.SetTip(InspectorReadPosts, readTip);
        ToolTip.SetShowOnDisabled(InspectorReadPosts, true);
        Avalonia.Automation.AutomationProperties.SetHelpText(InspectorReadPosts, readTip);
        InspectorGallery.IsEnabled = ready;
        InspectorHtmlExport.IsEnabled = ready;
    }

    private async void OnInspectorReadPosts(object? sender, RoutedEventArgs e)
    {
        DownloadMenuTarget? target = CaptureDownloadMenu(false);
        if (target == null || target.Threads.Length != 1 || WorkspaceCommandBusy || _selectionCommandRunning) return;
        await OpenFrozenReaderAsync(target, sender as Control ?? DownloadList(false));
    }

    private async void OnInspectorGallery(object? sender, RoutedEventArgs e)
    {
        DownloadMenuTarget? target = CaptureDownloadMenu(false);
        if (target == null || WorkspaceCommandBusy || _selectionCommandRunning) return;
        try { await OpenGalleryAsync(target.Lead, origin: sender as Control); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "inspector.gallery")) { /* Logged and shown. */ }
    }

    private async void OnInspectorHtmlExport(object? sender, RoutedEventArgs e)
    {
        DownloadMenuTarget? target = CaptureDownloadMenu(false);
        if (target == null || WorkspaceCommandBusy || _selectionCommandRunning) return;
        await ExportFrozenThreadsAsync(new[] { target.Lead }, sender as Control);
    }
}
