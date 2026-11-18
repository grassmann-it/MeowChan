using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

// Links dropped anywhere on the workspace are submitted like the Add line's text; dropped files and
// folders open the import dialog prefilled with them (contract manual-import-v1). Platform notes (Avalonia 12.1.4, not natively verified here):
// X11/XWayland uses XDND; browsers offer text/plain or UTF8_STRING beside text/uri-list, which
// Avalonia reads as files (file: entries only). Drops from native Wayland apps cross the
// compositor bridge. Windows uses OLE (CF_UNICODETEXT, CF_HDROP). macOS Finder drops arrive
// as file URLs; Safari may offer only public.url or promised files, which are not files here.
public partial class MainWindow
{
    private IDataTransfer? _dropTransfer;
    private DroppedContent? _dropContent;
    private DroppedContentKind? _dropOverlayKind;
    private int _dropGeneration;

    // Same availability as a clipboard suggestion: no drop under dialogs, gallery, readers or busy work.
    private bool CanAcceptDrop => CanShowClipboardSuggestion && !ShellHelpOverlay.IsVisible;

    // Smoke diagnostics: which availability condition refuses a drop right now.
    internal string DropAvailabilityForTests => string.Join(",", new (string Name, bool Ok)[]
    {
        ("opened", _hasOpened), ("restored", _restoreCompleted), ("open", !_isClosing), ("idle", !_importing),
        ("commands", !WorkspaceCommandBusy), ("enabled", IsEnabled), ("ready", _session.State == DesktopSessionState.Ready),
        ("dialogs", !HasVisibleShellDialog()), ("decision", !HasBlockingPageDecision()), ("visible", IsVisible),
        ("state", WindowState != WindowState.Minimized), ("reader", !_largeReaderOpen), ("gallery", _gallery == null),
        ("readerDialog", _archivePage?.IsReaderDialogOpen != true), ("help", !ShellHelpOverlay.IsVisible)
    }.Where(item => !item.Ok).Select(item => item.Name));

    private void InitializeUrlDrop()
    {
        // AllowDrop is inherited from WorkspaceRoot, so every page control is a target and events bubble here.
        DragDrop.AddDragEnterHandler(WorkspaceRoot, OnWorkspaceDragOver);
        DragDrop.AddDragOverHandler(WorkspaceRoot, OnWorkspaceDragOver);
        DragDrop.AddDragLeaveHandler(WorkspaceRoot, OnWorkspaceDragLeave);
        DragDrop.AddDropHandler(WorkspaceRoot, OnWorkspaceDrop);
    }

    private void OnWorkspaceDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        ++_dropGeneration;
        DroppedContent? content = CanAcceptDrop ? ReadDrop(e.DataTransfer) : null;
        e.DragEffects = content?.Kind is DroppedContentKind.Links || content is { Kind: DroppedContentKind.Files, Paths.Count: > 0 }
            ? DroppedLinks.AcceptedEffect(e.DragEffects) : DragDropEffects.None;
        ShowDropOverlay(content?.Kind, content?.FileCount ?? 0);
    }

    private void OnWorkspaceDragLeave(object? sender, DragEventArgs e)
    {
        // Moving between child controls raises Leave and then Enter within one platform event.
        // Only a Leave that no later drag event follows ends the drag, so the overlay does not flicker.
        int generation = ++_dropGeneration;
        Dispatcher.UIThread.Post(() => { if (generation == _dropGeneration) EndDrop(); });
    }

    private async void OnWorkspaceDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        ++_dropGeneration;
        // Read before the first await: the platform disposes the transfer once this handler returns.
        DroppedContent? content = CanAcceptDrop ? ReadDrop(e.DataTransfer) : null;
        EndDrop();
        e.DragEffects = content?.Kind is DroppedContentKind.Links || content is { Kind: DroppedContentKind.Files, Paths.Count: > 0 }
            ? DroppedLinks.AcceptedEffect(e.DragEffects) : DragDropEffects.None;
        // Most platforms skip Drop after DragOver answered None; these notices cover the ones that do not.
        if (content is { Kind: DroppedContentKind.Files, Paths.Count: > 0 })
            OpenManualImportDialog(content.Paths);
        else if (content?.Kind == DroppedContentKind.Files)
            ShowNotification(Texts.T("drop.empty.title"), Texts.T("drop.files.unreadable"), NotificationType.Information, operation: "drop.files");
        else if (content?.Kind == DroppedContentKind.Empty)
            ShowNotification(Texts.T("drop.empty.title"), Texts.T("drop.empty.detail"), NotificationType.Information, operation: "drop.empty");
        else if (content?.Text is { } text)
            await SubmitDroppedLinksAsync(text);
    }

    private async Task SubmitDroppedLinksAsync(string text)
    {
        if (!_restoreCompleted || _isClosing || _importing || WorkspaceCommandBusy) return;
        // Results appear under the Add line. The drop never fills or clears the line's own draft.
        NavigateWorkspace(WorkspacePage.Downloads);
        _importing = true;
        try
        {
            UpdateInteractionState();
            await SubmitInputAsync(text);
        }
        finally
        {
            _importing = false;
            UpdateInteractionState();
        }
    }

    private DroppedContent ReadDrop(IDataTransfer transfer)
    {
        // One read per drag; DragOver repeats for every pointer move and X11 reads are round trips.
        if (_dropContent != null && ReferenceEquals(transfer, _dropTransfer)) return _dropContent;
        _dropTransfer = transfer;
        try
        {
            _dropContent = DroppedLinks.Read(transfer);
        }
        catch (Exception exception)
        {
            // Selection transfers can fail or time out. Never guess the content; never log it.
            Log.Warning("Dropped content could not be read: {Error}", exception.GetType().Name);
            _dropContent = DroppedContent.Empty;
        }
        return _dropContent;
    }

    private void EndDrop()
    {
        _dropTransfer = null;
        _dropContent = null;
        ShowDropOverlay(null);
    }

    private void ShowDropOverlay(DroppedContentKind? kind, int files = 0)
    {
        if (kind == _dropOverlayKind) return;
        _dropOverlayKind = kind;
        DropOverlay.IsVisible = kind != null;
        if (kind == null) return;
        string prefix = kind switch
        {
            DroppedContentKind.Links => "drop.links",
            DroppedContentKind.Files => "drop.files",
            _ => "drop.empty"
        };
        DropOverlayTitle.Text = kind == DroppedContentKind.Files ? Texts.Count("drop.files.title", files) : Texts.T(prefix + ".title", files);
        DropOverlayDetail.Text = Texts.T(prefix + ".detail");
        DropOverlayCard[!Border.BorderBrushProperty] = new DynamicResourceExtension(
            kind is DroppedContentKind.Links or DroppedContentKind.Files ? "Workspace.Accent" : "Workspace.Warning");
    }
}
