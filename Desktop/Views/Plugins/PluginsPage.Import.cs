using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

public partial class PluginsPage
{
    /// <summary>Test seam for the file dialog: returns the chosen file's path, or null when nothing was chosen.</summary>
    internal Func<Task<string?>>? PickPluginFile { get; set; }

    private void UpdateAddAction() => SetAction("PluginsAdd", "plugins.unavailable", _services != null);

    private async void OnAddClick(object? sender, RoutedEventArgs e) => await AddPluginAsync();

    /// <summary>"Add plugin…": choose a file, then copy it through <see cref="PluginFileImport"/>. Nothing is activated.</summary>
    internal async Task AddPluginAsync()
    {
        if (_ended || _busy || _services == null) return;
        string? path;
        try { path = PickPluginFile != null ? await PickPluginFile() : await PickPluginFileCoreAsync(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { if (!_ended) ErrorFailure("plugins.error.add", exception); return; }
        if (_ended || path == null) return;
        await ImportPluginFileAsync(path);
    }

    private async Task<string?> PickPluginFileCoreAsync()
    {
        Avalonia.Platform.Storage.IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanOpen) { ErrorLocalized("plugins.add.noPicker"); return null; }
        System.Collections.Generic.IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Texts.T("plugins.add.pickerTitle"), AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(Texts.T("plugins.add.pickerType")) { Patterns = new[] { "*" + PluginFileImport.SourceExtension } } }
        });
        try { return files.Count == 1 ? files[0].TryGetLocalPath() : null; }
        finally { foreach (IStorageFile file in files) file.Dispose(); }
    }

    internal async Task ImportPluginFileAsync(string path)
    {
        if (_ended || _busy || _services == null) return;
        string directory = _services.PluginDirectory;
        PluginFileImportResult? result = null;
        (long generation, CancellationToken token) = BeginOperation();
        try { result = await PluginFileImport.ImportAsync(path, directory, token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_ended) ErrorFailure("plugins.error.add", exception); }
        finally { FinishOperation(generation); }
        if (_ended || result == null) return;

        if (!result.Succeeded)
        {
            switch (result.Status)
            {
                case PluginFileImportStatus.Unsupported: ErrorLocalized("plugins.add.rejected.unsupported", result.FileName); break;
                case PluginFileImportStatus.Empty: ErrorLocalized("plugins.add.rejected.empty", result.FileName); break;
                case PluginFileImportStatus.TooLarge: ErrorLocalized("plugins.add.rejected.tooLarge", result.FileName, PluginFileImport.SourceLimitMiB); break;
                case PluginFileImportStatus.NotRegularFile: ErrorLocalized("plugins.add.rejected.notFile", result.FileName); break;
                case PluginFileImportStatus.NameTaken: ErrorLocalized("plugins.add.rejected.exists", result.FileName); break;
                default: ErrorFailure("plugins.error.add", result.Detail); break;
            }
            return;
        }

        // Show the new entry: the category must not hide it, and the list is read again (a plain refresh, which activates nothing).
        C<ComboBox>("PluginCategory").SelectedIndex = 0;
        await RefreshAsync();
        if (_ended) return;
        if (result.CandidateId != null) Select(result.CandidateId);
        // Selecting another plugin clears the status line, so the result is announced after it.
        SetTestStatus(result.Status == PluginFileImportStatus.Imported ? "plugins.add.done" : "plugins.add.alreadyThere", result.FileName);
    }
}
