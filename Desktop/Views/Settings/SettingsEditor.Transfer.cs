using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private MenuFlyout? _settingsMore;
    private CancellationTokenSource? _settingsExportCancellation;
    internal Task SettingsExportTask { get; private set; } = Task.CompletedTask;
    public string SettingsMoreLabel => Texts.T("settings.transfer.more");

    private void OnSettingsMoreClick(object? sender, RoutedEventArgs e)
    {
        if (_isClosed || !IsEditable || sender is not Button origin) return;
        _settingsMore?.Hide();
        var export = new MenuItem { Header = Texts.T("settings.transfer.export") };
        AutomationProperties.SetName(export, Texts.T("settings.transfer.export"));
        export.Click += (_, _) =>
        {
            _settingsMore?.Hide();
            SettingsExportTask = ExportSavedSettingsAsync();
        };
        _settingsMore = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, Items = { export } };
        _settingsMore.ShowAt(origin);
    }

    private async Task ExportSavedSettingsAsync()
    {
        if (_isClosed || !IsEditable) return;
        IsBusy = true; // synchronously excludes Save/Discard/reset and a second picker
        using var cancellation = new CancellationTokenSource();
        _settingsExportCancellation = cancellation;
        // Do not clear draft validation or saved/unsaved status merely for exporting (including Cancel).
        try
        {
            // This is the confirmed Config, never this editor's uncommitted draft properties.
            Config snapshot = SettingsExportUi.CaptureSavedSnapshot(_config);
            IStorageProvider? provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider == null) throw new NotSupportedException();
            await SettingsExportUi.ExportSnapshotAsync(snapshot, provider, DateTime.Now, cancellation.Token);
        }
        catch (OperationCanceledException) { /* Closing or cancelling a picker is not an export error. */ }
        catch (Exception)
        {
            // A provider exception may contain a profile path. Neither display nor log it.
            if (!_isClosed && !cancellation.IsCancellationRequested)
            {
                ErrorDetails = null;
                SetErrorMessage("settings.transfer.exportFailed");
            }
        }
        finally
        {
            _settingsExportCancellation = null;
            IsBusy = false;
        }
    }

    private void RaiseTransferTextsChanged()
    {
        Raise(new[] { nameof(SettingsMoreLabel) });
        // Avoid a stale-language menu while keeping an already open system picker untouched.
        _settingsMore?.Hide();
        _settingsMore = null;
    }

    private void EndSettingsTransferLifetime()
    {
        _settingsMore?.Hide();
        _settingsMore = null;
        _settingsExportCancellation?.Cancel();
        // A started provider write drains; it never gets abandoned for a close/cancel.
    }
}

/// <summary>Testable save-dialog boundary. No path conversion, persistence, or mutation of the editor draft.</summary>
internal static class SettingsExportUi
{
    // Config.Clone is an editor-baseline operation, not a read-only snapshot: its compatibility
    // getters materialize source profiles and it normalizes/couples otherwise portable values.
    internal static Config CaptureSavedSnapshot(Config saved) => SettingsTransfer.CreatePortableSnapshot(saved);

    internal static FilePickerSaveOptions SaveOptions(DateTime date) => new()
    {
        Title = Texts.T("settings.transfer.exportTitle"),
        SuggestedFileName = "meowchan-settings-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json",
        DefaultExtension = "json",
        ShowOverwritePrompt = true,
        FileTypeChoices = new[]
        {
            new FilePickerFileType(Texts.T("settings.transfer.exportFileType")) { Patterns = new[] { "*.json" } }
        }
    };

    // Only the IStorageFile returned by this Save picker authorizes a write/overwrite. Never reopen its path,
    // choose an alternate target or retry a failed provider write. Unsupported save dialogs fail visibly.
    internal static async Task ExportSnapshotAsync(Config snapshot, IStorageProvider provider, DateTime date, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!provider.CanSave) throw new NotSupportedException();
        IStorageFile? file = await provider.SaveFilePickerAsync(SaveOptions(date));
        if (file == null) return;
        await Task.Run(async () =>
        {
            using (file)
            {
                cancellation.ThrowIfCancellationRequested();
                // Finish serialization before opening/truncating the authorized target. All I/O runs off the UI thread.
                byte[] bytes = Encoding.UTF8.GetBytes(SettingsTransfer.Export(snapshot).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                cancellation.ThrowIfCancellationRequested();
                await using Stream stream = await file.OpenWriteAsync().ConfigureAwait(false);
                if (stream.CanSeek) { stream.Position = 0; stream.SetLength(0); }
                // After opening the target, finish and flush even if the editor closes, to avoid cancellation-torn JSON.
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}
