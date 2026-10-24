using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class ArchivePage
{
    private DesktopSessionHost? _cleanupHost;
    private Func<IReadOnlyCollection<ResourceId>>? _cleanupGalleryThreads;
    private Func<bool>? _cleanupShellAvailable;
    private ArchiveCleanupDialog? _cleanupDialog;
    private CommandRouter? _cleanupRouter;

    internal bool IsArchiveCleanupOpen => _cleanupDialog != null;
    internal void AttachArchiveCleanup(DesktopSessionHost host, Func<IReadOnlyCollection<ResourceId>>? galleryThreads = null,
        Func<bool>? shellAvailable = null)
    {
        _cleanupHost = host; _cleanupGalleryThreads = galleryThreads; _cleanupShellAvailable = shellAvailable;
        if (_cleanupRouter == null)
        {
            _cleanupRouter = new(this);
            _cleanupRouter.Register(new("archive.cleanup", this,
                () => !_ended && IsEffectivelyVisible && Control<Button>("ArchiveCleanupOpen").IsEffectivelyVisible,
                CanOpenArchiveCleanup, () => _ = OpenArchiveCleanupAsync(), Priority: 100));
            AddHandler(KeyDownEvent, RouteArchiveCleanupCommand, RoutingStrategies.Bubble);
        }
        UpdateArchiveCleanupAction();
    }
    private void RouteArchiveCleanupCommand(object? sender, KeyEventArgs e) => _cleanupRouter?.Route(e);
    private bool CanOpenArchiveCleanup() => !_ended && _available && !_busy && _cleanupDialog == null
        && _cleanupHost?.State == DesktopSessionState.Ready && !IsReaderDialogOpen && !_exportRunning
        && (_cleanupShellAvailable?.Invoke() ?? true);
    private void UpdateArchiveCleanupAction()
    {
        if (this.FindControl<Button>("ArchiveCleanupOpen") is not Button button) return;
        CommandBindings.Apply(button, "archive.cleanup", iconWithLabel: true);
        button.IsEnabled = CanOpenArchiveCleanup();
    }
    private IReadOnlyCollection<ResourceId> OpenArchiveCleanupThreads()
    {
        HashSet<ResourceId> ids = new(_cleanupGalleryThreads?.Invoke() ?? Array.Empty<ResourceId>());
        // Even a retained detail pane can hold decoded media or a pending read.
        if (_thread != null) ids.Add(_thread.Summary.Id);
        if (_exportThread != null && (_exportRunning || _exportLoading || _exportPicking
            || Control<Border>("ArchiveExportDialog").IsVisible))
        {
            ids.Add(_exportThread);
            if (_exportMembers != null) ids.UnionWith(_exportMembers);
        }
        return ids.ToArray();
    }
    private async void OnArchiveCleanupClick(object? sender, RoutedEventArgs e) => await OpenArchiveCleanupAsync();
    internal async Task OpenArchiveCleanupAsync()
    {
        if (!CanOpenArchiveCleanup() || _cleanupHost == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        Control origin = Control<Button>("ArchiveCleanupOpen");
        var dialog = new ArchiveCleanupDialog(_cleanupHost, OpenArchiveCleanupThreads, _openFolder, _lifetime.Token)
        {
            Width = Math.Max(360, Math.Min(820, owner.ClientSize.Width - 32)),
            Height = Math.Max(380, Math.Min(700, owner.ClientSize.Height - 32))
        };
        _cleanupDialog = dialog; UpdateArchiveCleanupAction();
        try
        {
            await dialog.ShowDialog(owner);
            if (!_ended && dialog.ArchiveChanged) await ActivateAsync();
        }
        catch (Exception error) { if (!_ended) Message(error.Message, true); }
        finally
        {
            _cleanupDialog = null;
            if (!_ended)
            {
                UpdateArchiveCleanupAction();
                if (origin.IsEffectivelyVisible && origin.IsEffectivelyEnabled) origin.Focus();
                else Control<ListBox>("ArchiveThreads").Focus();
            }
        }
    }
    private void EndArchiveCleanupLifetime()
    {
        _cleanupDialog?.RequestClose();
        _cleanupRouter?.Dispose(); _cleanupRouter = null;
        RemoveHandler(KeyDownEvent, RouteArchiveCleanupCommand);
    }
}
