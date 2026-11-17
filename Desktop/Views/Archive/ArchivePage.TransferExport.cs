using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private DesktopSessionHost? _transferExportHost;
    private ArchiveTransferExportDialog? _transferExportDialog;
    private CommandRouter? _transferExportRouter;

    internal Func<ArchiveTransferExportDialog, Window, Task>? TransferExportShowForTests { get; set; }
    internal Func<string, Task<string?>>? TransferExportChooseFileForTests { get; set; }
    internal bool IsTransferExportOpen => _transferExportDialog != null;

    internal void AttachTransferExport(DesktopSessionHost host)
    {
        _transferExportHost = host ?? throw new ArgumentNullException(nameof(host));
        if (_transferExportRouter == null)
        {
            _transferExportRouter = new(this);
            _transferExportRouter.Register(new("archive.transfer-export", this,
                // The page, not the button: a view may keep the button under the header's More.
                () => !_ended && IsEffectivelyVisible, CanOpenTransferExport, () => OpenActions.Start(OpenTransferExportAsync, "archive.transfer-export"), Priority: 100));
            AddHandler(KeyDownEvent, RouteTransferExportCommand, RoutingStrategies.Bubble);
        }
        UpdateTransferExportAction();
    }

    private void RouteTransferExportCommand(object? sender, KeyEventArgs e) => _transferExportRouter?.Route(e);

    private bool CanOpenTransferExport() => !_ended && _available && !_busy && !_urlTextExportBusy && _transferExportDialog == null
        && _transferPreviewDialog == null && _transferImportDialog == null && !_transferPreviewOpening
        && !IsArchiveCleanupOpen && !IsReaderDialogOpen && !_exportRunning
        && _transferExportHost?.State == DesktopSessionState.Ready;

    private void UpdateTransferExportAction()
    {
        if (this.FindControl<Button>("ArchiveTransferExport") is not Button button) return;
        CommandBindings.Apply(button, "archive.transfer-export", iconWithLabel: true);
        button.IsEnabled = CanOpenTransferExport();
    }

    // The three maintenance tools of the header exclude each other while one of their dialogs is open.
    private void UpdateTransferHeaderActions()
    {
        UpdateTransferPreviewAction();
        UpdateTransferExportAction();
        UpdateArchiveCleanupAction();
    }

    private async void OnTransferExportClick(object? sender, RoutedEventArgs e) => await OpenTransferExportAsync();

    internal async Task OpenTransferExportAsync()
    {
        if (!CanOpenTransferExport() || _transferExportHost == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        Control origin = ArchiveTransferExport;
        // The checked rows of the current results page, in list order; an empty selection offers the whole archive only.
        IReadOnlyList<ResourceId> selected = FrozenArchiveSelection();
        ArchiveTransferExportDialog? dialog = null;
        try
        {
            Func<string, Task<string?>> chooseFile = TransferExportChooseFileForTests ?? (async suggested =>
            {
                if (_ended || _transferExportDialog is not { } active || !active.StorageProvider.CanSave) return null;
                // No overwrite prompt: the package is create-only, so an existing name is refused with a clear message
                // instead of a "Replace?" question whose Yes could not be honoured.
                IStorageFile? file = await active.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Texts.T("transfer.export.choose"), SuggestedFileName = suggested,
                    DefaultExtension = ArchiveTransferExportDestination.Extension, ShowOverwritePrompt = false,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType(Texts.T("transfer.export.fileType")) { Patterns = new[] { "*." + ArchiveTransferExportDestination.Extension } }
                    }
                });
                try { return file?.TryGetLocalPath(); }
                finally { file?.Dispose(); }
            });
            dialog = new ArchiveTransferExportDialog(_transferExportHost, selected, chooseFile, _openFolder, _lifetime.Token);
            _transferExportDialog = dialog;
            UpdateTransferHeaderActions();
            if (TransferExportShowForTests is { } show) await show(dialog, owner);
            else await dialog.ShowDialog(owner);
        }
        catch (Exception) { if (!_ended) Message(Texts.T("transfer.export.error.write"), true); }
        finally
        {
            if (dialog != null)
            {
                dialog.Dispose();
                // Closing cancels the operation; wait until the host has let go of its reader and stage before re-enabling.
                try { await dialog.Completion; } catch { /* Reported inside the dialog. */ }
                try { dialog.Close(); } catch { /* Show may have failed before native creation. */ }
            }
            _transferExportDialog = null;
            if (!_ended)
            {
                UpdateTransferHeaderActions();
                if (ActionOriginFocusTarget(origin) is { IsEffectivelyEnabled: true } target) target.Focus();
                else ArchiveThreads.Focus();
            }
        }
    }

    private void EndTransferExportLifetime()
    {
        if (_transferExportDialog is { } dialog) { dialog.Terminate(); dialog.Close(); }
        _transferExportRouter?.Dispose(); _transferExportRouter = null;
        RemoveHandler(KeyDownEvent, RouteTransferExportCommand);
    }
}
