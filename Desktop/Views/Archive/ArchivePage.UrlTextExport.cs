using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private bool _urlTextExportBusy;
    private CancellationTokenSource? _urlTextExportCancellation;
    private UrlTextExportDialog? _urlTextExportDialog;
    internal Task UrlTextExportTask { get; private set; } = Task.CompletedTask;
    internal Func<ResourceId, CancellationToken, Task<ArchiveThreadSummary?>>? UrlTextSummaryForTests { get; set; }
    internal Func<UrlTextExportDialog, Window, Task>? UrlTextShowForTests { get; set; }
    internal Func<Task<string?>>? UrlTextChooseForTests { get; set; }
    internal Func<ArchiveUrlTextExportPlan, string, CancellationToken, Task<UrlTextExportFileWriteResult>>? UrlTextWriteForTests { get; set; }

    private bool UrlTextOtherModalOpen => IsArchiveCleanupOpen
        || Control<Border>("ArchiveExportDialog").IsVisible || _exportRunning
        || _transferPreviewOpening || _transferPreviewDialog != null;
    private bool CanOpenUrlTextExport => !_ended && !_busy && _available
        && !_urlTextExportBusy && _openReader != null && !UrlTextOtherModalOpen;

    internal Task OpenUrlTextExportForThreadAsync(ResourceId id, Control? origin)
    {
        if (!CanOpenUrlTextExport || TopLevel.GetTopLevel(this) is not Window owner)
            return Task.CompletedTask;
        // The lock is acquired synchronously, before any reader/show/picker await.
        _urlTextExportBusy = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _urlTextExportCancellation = cancellation;
        return UrlTextExportTask = RunUrlTextExportAsync(id, origin, owner, cancellation);
    }

    private async Task RunUrlTextExportAsync(ResourceId id, Control? origin, Window owner,
        CancellationTokenSource cancellation)
    {
        UrlTextExportDialog? dialog = null;
        void OwnerClosing(object? sender, WindowClosingEventArgs e) => cancellation.Cancel();
        owner.Closing += OwnerClosing;
        try
        {
            // Runs synchronously after lock acquisition, before the first read await.
            UpdateActions();
            ArchiveThreadSummary? summary;
            if (UrlTextSummaryForTests is { } read) summary = await read(id, cancellation.Token);
            else
            {
                await using ArchiveReader reader = await _openReader!(cancellation.Token);
                summary = await reader.ReadThreadSummaryAsync(id, cancellation.Token);
            }
            if (_ended || !_available || _busy || cancellation.IsCancellationRequested || UrlTextOtherModalOpen || !owner.IsVisible) return;
            if (summary == null) { Message(Texts.T("urltext.export.error.summary"), true); return; }
            var plan = ArchiveUrlTextExportPlan.TryCreate(id, summary, out _);
            if (plan == null) { Message(Texts.T("urltext.export.error.invalidUrl"), true); return; }
            dialog = new UrlTextExportDialog(plan, async () =>
            {
                if (UrlTextChooseForTests is { } choose) return await choose();
                if (_ended || cancellation.IsCancellationRequested || _urlTextExportDialog is not { } active
                    || !active.StorageProvider.CanSave) return null;
                IStorageFile? file = await active.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Texts.T("urltext.export.save"), SuggestedFileName = "page-links.txt",
                    DefaultExtension = "txt", FileTypeChoices = new[]
                    {
                        new FilePickerFileType(Texts.T("urltext.export.fileType")) { Patterns = new[] { "*.txt" } }
                    }
                });
                try
                {
                    if (file == null) return null;
                    return file.TryGetLocalPath() ?? throw new InvalidOperationException();
                }
                finally { file?.Dispose(); }
            }, UrlTextWriteForTests ?? ((planToWrite, target, token) =>
                new UrlTextExportFileWriter().WriteAsync(planToWrite, target, token)), cancellation.Token);
            _urlTextExportDialog = dialog;
            if (UrlTextShowForTests is { } show) await show(dialog, owner);
            else await dialog.ShowDialog(owner);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_ended && !cancellation.IsCancellationRequested) Message(Texts.T(dialog == null ? "urltext.export.error.summary" : "urltext.export.error.write"), true); }
        finally
        {
            if (dialog != null)
            {
                await dialog.TerminateAsync();
                if (!_ended && !cancellation.IsCancellationRequested && owner.IsVisible
                    && dialog.Operation.Result is { Status: UrlTextExportFileWriteStatus.PartialRetained } result)
                    Message(Texts.T(result.Error == UrlTextExportFileWriteError.CreationUncertain
                        ? "urltext.export.creationUncertain" : "urltext.export.partialRetained"), true);
                try { dialog.Close(); } catch { /* Show may have failed before native creation. */ }
            }
            owner.Closing -= OwnerClosing;
            _urlTextExportDialog = null;
            _urlTextExportCancellation = null;
            cancellation.Dispose();
            _urlTextExportBusy = false;
            if (!_ended) UpdateActions();
            if (!_ended && owner.IsVisible && origin is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) origin.Focus();
        }
    }

    private void EndUrlTextExportLifetime()
    {
        _urlTextExportCancellation?.Cancel();
        // Terminate never abandons a started write; RunUrlTextExportAsync awaits its drain.
        if (_urlTextExportDialog is { } dialog)
        {
            _ = dialog.TerminateAsync();
            dialog.Close();
        }
    }
}
