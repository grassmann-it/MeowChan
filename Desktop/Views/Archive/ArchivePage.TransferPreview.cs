using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private NativeTransferPreviewDialog? _transferPreviewDialog;
    private bool _transferPreviewOpening;
    private CommandRouter? _transferPreviewRouter;

    // Synthetic native-window fixtures only. Production always uses the real picker/show.
    internal Func<NativeTransferPreviewSession, Func<Task<string?>>, NativeTransferPreviewDialog>?
        TransferPreviewDialogFactoryForTests { get; set; }
    internal Func<NativeTransferPreviewDialog, Window, Task>? TransferPreviewShowForTests { get; set; }
    internal Func<Task<string?>>? TransferPreviewChooseFileForTests { get; set; }
    internal Action<string>? TransferPreviewWorkDirectoryCreatedForTests { get; set; }

    private void InitializeTransferPreview()
    {
        _transferPreviewRouter = new(this);
        _transferPreviewRouter.Register(new("archive.transfer-preview", this,
            () => !_ended && IsEffectivelyVisible, () => CanOpenTransferPreview,
            () => _ = OpenTransferPreviewAsync(), Priority: 100));
        AddHandler(KeyDownEvent, RouteTransferPreviewCommand, RoutingStrategies.Bubble);
    }

    private void RouteTransferPreviewCommand(object? sender, KeyEventArgs e) => _transferPreviewRouter?.Route(e);

    private bool CanOpenTransferPreview => !_ended && !_busy && _available && !_urlTextExportBusy
        && !_transferPreviewOpening && _transferPreviewDialog == null && !IsArchiveCleanupOpen
        && !IsReaderDialogOpen && !_exportRunning;

    private void UpdateTransferPreviewAction()
    {
        Button button = Control<Button>("ArchiveTransferPreview");
        CommandBindings.Apply(button, "archive.transfer-preview", iconWithLabel: true);
        button.IsEnabled = CanOpenTransferPreview;
    }

    private async void OnTransferPreviewClick(object? sender, RoutedEventArgs e) => await OpenTransferPreviewAsync();

    internal async Task OpenTransferPreviewAsync()
    {
        if (!CanOpenTransferPreview || TopLevel.GetTopLevel(this) is not Window owner) return;
        Control origin = Control<Button>("ArchiveTransferPreview");
        NativeTransferPreviewSession? session = null;
        NativeTransferPreviewDialog? dialog = null;
        string? workDirectory = null;
        _transferPreviewOpening = true;
        UpdateTransferPreviewAction();
        try
        {
            // A fresh operation-owned temporary root, never a package/UI path or media/profile root.
            // Preview has no profile writer; cleanup can safely drain after the owning window
            // closes without retaining or writing to a profile that may be shutting down.
            workDirectory = CreateTransferPreviewWorkDirectory();
            TransferPreviewWorkDirectoryCreatedForTests?.Invoke(workDirectory);
            session = new NativeTransferPreviewSession(workDirectory);
            Func<Task<string?>> chooseFile = TransferPreviewChooseFileForTests ?? (async () =>
            {
                if (_ended || _transferPreviewDialog == null || !_transferPreviewDialog.StorageProvider.CanOpen) return null;
                var files = await _transferPreviewDialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Texts.T("transfer.preview.choose"), AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType(Texts.T("transfer.preview.fileType")) { Patterns = new[] { "*.zip" } } }
                });
                try { return files.Count == 1 ? files[0].TryGetLocalPath() : null; }
                finally { foreach (IStorageFile file in files) file.Dispose(); }
            });
            // Ownership transfers only after a fully constructed dialog. Before that,
            // the page remains responsible for the otherwise unowned session.
            dialog = TransferPreviewDialogFactoryForTests?.Invoke(session, chooseFile)
                ?? new NativeTransferPreviewDialog(session, chooseFile);
            _transferPreviewDialog = dialog;
            _transferPreviewOpening = false;
            UpdateTransferPreviewAction();
            if (TransferPreviewShowForTests is { } show) await show(dialog, owner);
            else await dialog.ShowDialog(owner);

        }
        catch (Exception)
        {
            if (!_ended) Message(Texts.T("transfer.preview.error.read"), true);
        }
        finally
        {
            bool retained = false;
            if (dialog != null)
            {
                // Covers normal close, page lifetime end and ShowDialog failure alike.
                // Do not depend on a native Closed event when creation/show failed.
                try
                {
                    Task cleanup = dialog.TerminateAsync();
                    // A show failure can occur after partial native creation. End that
                    // window too; do not leave a disabled orphan attached to its owner.
                    try { dialog.Close(); }
                    catch { retained = true; }
                    await cleanup;
                }
                catch { retained = true; }
            }
            else if (session != null)
            {
                try { await session.DisposeAsync(); }
                catch { retained = true; }
            }
            if (session != null) retained |= session.Current.CleanupRetained;
            if (workDirectory != null)
            {
                try
                {
                    ArchiveExportMetadataFile.RejectLinks(workDirectory);
                    Directory.Delete(workDirectory, recursive: false);
                }
                catch { retained = true; }
            }
            _transferPreviewDialog = null;
            _transferPreviewOpening = false;
            if (!_ended)
            {
                UpdateTransferPreviewAction();
                if (retained) Message(Texts.T("transfer.preview.cleanupRetained"), true);
                if (origin.IsEffectivelyVisible && origin.IsEffectivelyEnabled) origin.Focus();
            }
        }
    }

    internal static string CreateTransferPreviewWorkDirectory()
    {
        // No caller-selected parent: this fresh window-operation root is the ownership
        // context. Reject links before the first write; never reuse an existing directory.
        ArchiveExportMetadataFile.RejectLinks(Path.GetTempPath());
        string directory = Directory.CreateTempSubdirectory("meowchan-transfer-preview-").FullName;
        // .NET creates owner-only permissions (0700) on Unix. On Windows this uses
        // the user's temp ACLs; this is not a hostile-host or cross-account sandbox.
        try { ArchiveExportMetadataFile.RejectLinks(directory); return directory; }
        catch { try { Directory.Delete(directory, recursive: false); } catch { } throw; }
    }

    private void EndTransferPreviewLifetime()
    {
        if (_transferPreviewDialog is { } dialog)
        {
            // Invalidate picker/render and detach global events before closing the window.
            // The opening operation's finally path awaits this same terminal drain task.
            _ = dialog.TerminateAsync();
            dialog.Close();
        }
        _transferPreviewRouter?.Dispose();
        _transferPreviewRouter = null;
        RemoveHandler(KeyDownEvent, RouteTransferPreviewCommand);
    }
}
