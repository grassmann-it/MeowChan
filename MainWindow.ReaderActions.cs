using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class MainWindow
{
    private void SetupInspectorLocalActions()
    {
        CommandBindings.Apply(WorkspaceControl<Button>("InspectorReadPosts"), "thread.read", iconWithLabel: true);
        CommandBindings.Apply(WorkspaceControl<Button>("InspectorGallery"), "gallery.open", iconWithLabel: true);
        CommandBindings.Apply(WorkspaceControl<Button>("InspectorHtmlExport"), "export.open-html-dialog", iconWithLabel: true);
    }

    private void RefreshInspectorLocalActions()
    {
        bool thread = GetWorkspaceSelection() is Desktop.ThreadRow;
        WorkspaceControl<WrapPanel>("InspectorLocalActions").IsVisible = thread;
        bool ready = thread && _restoreCompleted && !WorkspaceCommandBusy && !_selectionCommandRunning;
        WorkspaceControl<Button>("InspectorReadPosts").IsEnabled = ready && SelectedThreads().Length == 1;
        WorkspaceControl<Button>("InspectorGallery").IsEnabled = ready;
        WorkspaceControl<Button>("InspectorHtmlExport").IsEnabled = ready;
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
        await OpenGalleryAsync(target.Lead, origin: sender as Control);
    }

    private async void OnInspectorHtmlExport(object? sender, RoutedEventArgs e)
    {
        DownloadMenuTarget? target = CaptureDownloadMenu(false);
        if (target == null || WorkspaceCommandBusy || _selectionCommandRunning) return;
        await ExportFrozenThreadsAsync(new[] { target.Lead }, sender as Control);
    }
}
