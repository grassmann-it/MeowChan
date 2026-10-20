using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private bool _exportRunning;
    private bool _exportPicking;
    private CancellationTokenSource? _exportCancellation;
    private ResourceId? _exportThread;
    internal ArchiveHtmlExportResult? LastExport { get; private set; }
    internal bool IsExportRunning => _exportRunning;

    private void LocalizeExport()
    {
        Control<CheckBox>("ArchiveExportMedia").Content = Texts.T("archive.exportMedia");
        Control<Button>("ArchiveExportClose").Content = Texts.T(_exportRunning ? "archive.exportCancel" : "action.close");
    }
    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_thread == null || _busy || _ended || _exportRunning) return;
        _exportThread = _thread.Summary.Id;
        LastExport = null;
        string parent = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!Directory.Exists(parent)) parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Control<TextBox>("ArchiveExportDestination").Text = Path.Combine(parent, ExportFolderName());
        Control<TextBlock>("ArchiveExportSummary").Text = _thread.Summary.Title + " · " + Texts.T("archive.postsCount", _thread.PostCount);
        Control<TextBlock>("ArchiveExportStatus").Text = string.Empty;
        Control<Border>("ArchiveExportDialog").IsVisible = true;
        UpdateExportActions();
        Control<TextBox>("ArchiveExportDestination").Focus();
    }
    private string ExportFolderName()
    {
        string identity = _exportThread?.Source + "-" + _exportThread?.Board + "-" + _exportThread?.Thread;
        string safe = new(identity.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_').Take(120).ToArray());
        return "meowchan-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    }
    private async void OnExportBrowse(object? sender, RoutedEventArgs e)
    {
        if (_exportRunning || _exportPicking || _ended) return;
        _exportPicking = true;
        UpdateExportActions();
        try
        {
            IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null || !storage.CanPickFolder) { ExportStatus(Texts.T("settings.pickerUnavailable")); return; }
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = Texts.T("archive.exportBrowse"), AllowMultiple = false });
            try
            {
                if (_ended || !Control<Border>("ArchiveExportDialog").IsVisible || folders.Count == 0) return;
                string? parent = folders[0].TryGetLocalPath();
                if (parent == null) { ExportStatus(Texts.T("settings.pickerLocalOnly")); return; }
                Control<TextBox>("ArchiveExportDestination").Text = Path.Combine(parent, ExportFolderName());
            }
            finally { foreach (IStorageFolder folder in folders) folder.Dispose(); }
        }
        catch (Exception exception) { if (!_ended) ExportStatus(exception.Message); }
        finally { _exportPicking = false; if (!_ended) UpdateExportActions(); }
    }
    private async void OnExportStart(object? sender, RoutedEventArgs e)
    {
        if (_exportRunning || _exportPicking || _ended || _exportThread == null || _openReader == null) return;
        _exportRunning = true;
        LastExport = null;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportCancellation = cancellation;
        UpdateExportActions();
        UpdateActions();
        ExportStatus(Texts.T("archive.exportWorking"));
        try
        {
            await using ArchiveReader reader = await _openReader(cancellation.Token);
            Progress<ArchiveHtmlExportProgress> progress = new(value =>
            {
                if (!_ended && ReferenceEquals(_exportCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    ExportStatus(Texts.T(value.Phase == ArchiveHtmlExportPhase.ReadingMedia ? "archive.exportReading" : "archive.exportProgress", value.PostsWritten, value.FilesCopied));
            });
            ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, _exportThread,
                Control<TextBox>("ArchiveExportDestination").Text ?? string.Empty,
                new ArchiveHtmlExportOptions(Control<CheckBox>("ArchiveExportMedia").IsChecked == true), progress, cancellation.Token);
            if (_ended) return;
            LastExport = result;
            ExportStatus(Texts.T("archive.exportDone", result.PostCount, result.FileCount, result.Omissions.Count, result.Warnings.Count) + "\n" + result.IndexPath);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_ended) ExportStatus(Texts.T("archive.exportCancelled")); }
        catch (Exception exception) { if (!_ended) ExportStatus(Texts.T("archive.exportFailed") + "\n" + exception.Message); }
        finally
        {
            _exportCancellation = null;
            _exportRunning = false;
            if (!_ended) { UpdateExportActions(); UpdateActions(); }
        }
    }
    private void UpdateExportActions()
    {
        bool ready = !_exportRunning && !_exportPicking;
        Control<Button>("ArchiveExportStart").IsEnabled = ready;
        Control<Button>("ArchiveExportBrowse").IsEnabled = ready;
        Control<TextBox>("ArchiveExportDestination").IsEnabled = ready;
        Control<CheckBox>("ArchiveExportMedia").IsEnabled = ready;
        Control<Button>("ArchiveExportOpen").IsVisible = LastExport != null && !_exportRunning;
        LocalizeExport();
    }
    private void ExportStatus(string message) => Control<TextBlock>("ArchiveExportStatus").Text = message;
    private void OnExportClose(object? sender, RoutedEventArgs e)
    {
        if (_exportRunning) { _exportCancellation?.Cancel(); ExportStatus(Texts.T("archive.exportCancelling")); return; }
        if (_exportPicking) return;
        Control<Border>("ArchiveExportDialog").IsVisible = false;
        Control<Button>("ExportArchiveButton").Focus();
    }
    private void OnExportOpen(object? sender, RoutedEventArgs e)
    { if (!_exportRunning && LastExport != null) _openFolder?.Invoke(LastExport.Directory); }
    private void OnExportKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; OnExportClose(sender, e); } }
}
