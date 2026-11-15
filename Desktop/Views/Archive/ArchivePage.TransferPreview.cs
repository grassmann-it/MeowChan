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
    private DesktopSessionHost? _transferImportHost;
    private NativeTransferPreviewDialog? _transferPreviewDialog;
    private NativeTransferImportDialog? _transferImportDialog;
    private bool _transferPreviewOpening;
    private CommandRouter? _transferPreviewRouter;

    // Synthetic native-window fixtures only. Production uses the host-backed import dialog
    // once MainWindow attaches its ready DesktopSessionHost. The preview factory remains for
    // passive inventory/lifecycle tests and for a page that has no profile host yet.
    internal Func<NativeTransferPreviewSession, Func<Task<string?>>, NativeTransferPreviewDialog>?
        TransferPreviewDialogFactoryForTests { get; set; }
    internal Func<NativeTransferPreviewDialog, Window, Task>? TransferPreviewShowForTests { get; set; }
    internal Func<Task<string?>>? TransferPreviewChooseFileForTests { get; set; }
    internal Action<string>? TransferPreviewWorkDirectoryCreatedForTests { get; set; }

    internal Func<NativeTransferImportSession, Func<Task<string?>>, NativeTransferImportDialog>?
        TransferImportDialogFactoryForTests { get; set; }
    internal Func<NativeTransferImportDialog, Window, Task>? TransferImportShowForTests { get; set; }
    internal Func<Task<string?>>? TransferImportChooseFileForTests { get; set; }
    internal Action<string>? TransferImportWorkDirectoryCreatedForTests { get; set; }

    internal void AttachTransferImport(DesktopSessionHost host)
    {
        _transferImportHost = host ?? throw new ArgumentNullException(nameof(host));
        UpdateTransferPreviewAction();
    }

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
        && !_transferPreviewOpening && _transferPreviewDialog == null && _transferImportDialog == null
        && !IsArchiveCleanupOpen && !IsReaderDialogOpen && !_exportRunning && !IsTransferExportOpen
        && (_transferImportHost == null || _transferImportHost.State == DesktopSessionState.Ready);

    private void UpdateTransferPreviewAction()
    {
        Button button = ArchiveTransferPreview;
        CommandBindings.Apply(button, "archive.transfer-preview", iconWithLabel: true);
        button.IsEnabled = CanOpenTransferPreview;
    }

    private async void OnTransferPreviewClick(object? sender, RoutedEventArgs e) => await OpenTransferPreviewAsync();

    internal async Task OpenTransferPreviewAsync()
    {
        if (!CanOpenTransferPreview || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_transferImportHost != null)
        {
            await OpenTransferImportAsync(owner);
            return;
        }
        await OpenPreviewOnlyAsync(owner);
    }

    private async Task OpenTransferImportAsync(Window owner)
    {
        Control origin = ArchiveTransferPreview;
        NativeTransferImportSession? session = null;
        NativeTransferImportDialog? dialog = null;
        string? workDirectory = null;
        _transferPreviewOpening = true;
        UpdateTransferPreviewAction();
        try
        {
            workDirectory = CreateTransferImportWorkDirectory();
            TransferImportWorkDirectoryCreatedForTests?.Invoke(workDirectory);
            session = _transferImportHost!.CreateNativeTransferImportSession(workDirectory);
            Func<Task<string?>> chooseFile = TransferImportChooseFileForTests ?? (async () =>
            {
                if (_ended || _transferImportDialog == null || !_transferImportDialog.StorageProvider.CanOpen) return null;
                var files = await _transferImportDialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Texts.T("transfer.import.choose"), AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType(Texts.T("transfer.import.fileType")) { Patterns = new[] { "*.zip" } } }
                });
                try { return files.Count == 1 ? files[0].TryGetLocalPath() : null; }
                finally { foreach (IStorageFile file in files) file.Dispose(); }
            });
            dialog = TransferImportDialogFactoryForTests?.Invoke(session, chooseFile)
                ?? new NativeTransferImportDialog(session, chooseFile);
            _transferImportDialog = dialog;
            _transferPreviewOpening = false;
            UpdateTransferPreviewAction();
            if (TransferImportShowForTests is { } show) await show(dialog, owner);
            else await dialog.ShowDialog(owner);
        }
        catch (Exception)
        {
            if (!_ended) Message(Texts.T("transfer.import.error.read"), true);
        }
        finally
        {
            bool retained = false;
            if (dialog != null)
            {
                try
                {
                    Task cleanup = dialog.TerminateAsync();
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
            if (workDirectory != null) retained |= !TryDeleteTransferWorkDirectory(workDirectory);
            _transferImportDialog = null;
            _transferPreviewOpening = false;
            if (!_ended)
            {
                UpdateTransferPreviewAction();
                if (retained) Message(Texts.T("transfer.import.cleanupRetained"), true);
                if (ActionOriginFocusTarget(origin) is { IsEffectivelyEnabled: true } target) target.Focus();
            }
        }
    }

    private async Task OpenPreviewOnlyAsync(Window owner)
    {
        Control origin = ArchiveTransferPreview;
        NativeTransferPreviewSession? session = null;
        NativeTransferPreviewDialog? dialog = null;
        string? workDirectory = null;
        _transferPreviewOpening = true;
        UpdateTransferPreviewAction();
        try
        {
            // A fresh operation-owned temporary root, never a package/UI path or media/profile root.
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
                try
                {
                    Task cleanup = dialog.TerminateAsync();
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
            if (workDirectory != null) retained |= !TryDeleteTransferWorkDirectory(workDirectory);
            _transferPreviewDialog = null;
            _transferPreviewOpening = false;
            if (!_ended)
            {
                UpdateTransferPreviewAction();
                if (retained) Message(Texts.T("transfer.preview.cleanupRetained"), true);
                if (ActionOriginFocusTarget(origin) is { IsEffectivelyEnabled: true } target) target.Focus();
            }
        }
    }

    internal static string CreateTransferPreviewWorkDirectory() => CreateTransferWorkDirectory("meowchan-transfer-preview-");
    internal static string CreateTransferImportWorkDirectory() => CreateTransferWorkDirectory("meowchan-transfer-import-");

    private static string CreateTransferWorkDirectory(string prefix)
    {
        // No caller-selected parent: this fresh window-operation root is the ownership
        // context. Reject links before the first write; never reuse an existing directory.
        ArchiveExportMetadataFile.RejectLinks(Path.GetTempPath());
        string directory = Directory.CreateTempSubdirectory(prefix).FullName;
        try { ArchiveExportMetadataFile.RejectLinks(directory); return directory; }
        catch { try { Directory.Delete(directory, recursive: false); } catch { } throw; }
    }

    private static bool TryDeleteTransferWorkDirectory(string directory)
    {
        try
        {
            ArchiveExportMetadataFile.RejectLinks(directory);
            Directory.Delete(directory, recursive: false);
            return true;
        }
        catch { return false; }
    }

    private void EndTransferPreviewLifetime()
    {
        if (_transferImportDialog is { } import)
        {
            _ = import.TerminateAsync();
            import.Close();
        }
        if (_transferPreviewDialog is { } preview)
        {
            _ = preview.TerminateAsync();
            preview.Close();
        }
        _transferPreviewRouter?.Dispose();
        _transferPreviewRouter = null;
        RemoveHandler(KeyDownEvent, RouteTransferPreviewCommand);
    }
}
