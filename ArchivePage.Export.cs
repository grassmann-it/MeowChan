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
using MeowChan.Persistence;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private bool _exportRunning;
    private bool _exportPicking;
    private bool _exportLoading;
    private bool _exportPickerOpen;
    private CancellationTokenSource? _exportLookupCancellation;
    private CancellationTokenSource? _exportCancellation;
    private ResourceId? _exportThread;
    private int _exportRequest;
    private string _exportNewDestination = string.Empty;
    internal Func<string, Task>? OpenExportFolder { get; set; }
    internal ExportHistoryStore? ExportHistory { get; set; }
    internal ArchiveHtmlExportResult? LastExport { get; private set; }
    internal bool IsExportRunning => _exportRunning;
    private sealed record PreviousExport(string Destination, DateTimeOffset Date, ArchiveExportInspection? Inspection)
    {
        public override string ToString() => (Inspection?.Metadata.AdoptedLegacy == true ? Texts.T("export.associatedDate") + " " : string.Empty) + Date.ToLocalTime().ToString("g", Texts.Culture) + " · " + Destination + (Inspection == null ? " · " + Texts.T("export.missing") : string.Empty);
    }
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        ResourceId? id = _selectedThread ?? _thread?.Summary.Id;
        if (id != null) await OpenExportForThreadAsync(id, sender as Control);
    }
    internal string LastExportDirectoryPreference { get; set; } = string.Empty;
    internal Func<string, Task>? SaveExportDirectory { get; set; }
    private async Task RememberExportDirectoryAsync(string directory)
    {
        if (SaveExportDirectory != null) await SaveExportDirectory(directory);
        LastExportDirectoryPreference = directory;
    }
    internal Task OpenExportForThreadAsync(ResourceId id, Control? origin) => OpenExportCoreAsync(id, origin, null, null);
    private async Task OpenExportCoreAsync(ResourceId id, Control? origin, ResourceId[]? members, Action? onClosed,
        bool allResults = false, bool resolvedResults = false)
    {
        if (_ended || _openReader == null) return;
        EnsureExportDialog();
        if (Control<Border>("ArchiveExportDialog").IsVisible && !resolvedResults) { Control<Button>("ArchiveExportClose").Focus(); return; }
        int request = ++_exportRequest;
        _exportThread = id; _exportOrigin = origin; LastExport = null;
        _exportAllResults = allResults; _exportResolving = false;
        _exportMembers = members; _exportClosed = onClosed; LastCollectionExport = null;
        _exportLookupCancellation?.Cancel(); _exportLookupCancellation?.Dispose();
        _exportLookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Control<TextBox>("ArchiveExportDestination").IsEnabled = true;
        _exportNewDestination = string.Empty;
        Control<TextBox>("ArchiveExportDestination").Text = string.Empty;
        _exportLoading = true;
        UpdateExportSummary();
        Control<ComboBox>("ArchiveExportHistory").ItemsSource = Array.Empty<PreviousExport>();
        Control<ComboBox>("ArchiveExportLegacy").ItemsSource = Array.Empty<string>();
        Control<RadioButton>("ArchiveExportNew").IsChecked = true;
        Control<CheckBox>("ArchiveExportBackup").IsChecked = false;
        ExportStatus(string.Empty);
        ShowExportDialog();
        UpdateExportMode(); Control<TextBox>("ArchiveExportDestination").Focus();
        try
        {
            string destination = await Task.Run(() =>
            {
                string parent = LastExportDirectoryPreference;
                if (!Directory.Exists(parent)) parent = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!Directory.Exists(parent)) parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return FreshSelectedExportDestination(parent, id);
            });
            if (_ended || request != _exportRequest) return;
            _exportNewDestination = destination;
            Control<TextBox>("ArchiveExportDestination").Text = destination;
            await ReloadSelectedExportHistoryAsync(id, request);
        }
        catch (Exception exception) { if (!_ended) ExportStatus(exception.Message); }
        finally
        {
            if (request == _exportRequest) _exportLoading = false;
            if (!_ended)
            {
                UpdateExportActions();
                if (request == _exportRequest && Control<Border>("ArchiveExportDialog").IsVisible)
                    Control<TextBox>("ArchiveExportDestination").Focus();
            }
        }
    }
    private async Task ReloadExportHistoryAsync(ResourceId id, int request)
    {
        if (ExportHistory == null) return;
        string? selectedDestination = SelectedExport?.Destination;
        try
        {
            CancellationToken lookup = _exportLookupCancellation?.Token ?? _lifetime.Token;
            var entries = await Task.Run(() => ExportHistory.ListVerifiedAsync(id, lookup));
            if (_ended || request != _exportRequest || !Control<Border>("ArchiveExportDialog").IsVisible) return;
            PreviousExport[] choices = entries.OrderBy(item => item.Inspection == null).Select(item => new PreviousExport(item.Entry.Destination, item.Entry.ExportedAt, item.Inspection)).ToArray();
            Control<ComboBox>("ArchiveExportHistory").ItemsSource = choices;
            int selected = Array.FindIndex(choices, item => item.Destination == selectedDestination);
            Control<ComboBox>("ArchiveExportHistory").SelectedIndex = selected >= 0 ? selected : choices.Length > 0 ? 0 : -1;
            UpdateExportMode();
        }
        catch (Exception exception)
        {
            if (!_ended && request == _exportRequest)
                ExportStatus(Control<TextBlock>("ArchiveExportStatus").Text + "\n" + Texts.T("export.historyError", exception.Message));
        }
    }
    internal static string FreshExportDestination(string parent, ResourceId id)
    {
        string identity = id.Source + "-" + id.Board + "-" + id.Thread;
        string safe = new(identity.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_').Take(120).ToArray());
        string stem = Path.Combine(parent, "meowchan-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string candidate = stem;
        for (int suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++) candidate = stem + "-" + suffix;
        return candidate;
    }
    private PreviousExport? SelectedExport => Control<ComboBox>("ArchiveExportHistory").SelectedItem as PreviousExport;
    private bool UpdatingExport => Control<RadioButton>("ArchiveExportUpdate").IsChecked == true;
    private void UpdateExportMode()
    {
        if (_exportCard == null) return;
        bool updating = UpdatingExport;
        TextBox target = Control<TextBox>("ArchiveExportDestination");
        if (updating && SelectedExportDestination != null)
        {
            if (target.IsEnabled) _exportNewDestination = target.Text ?? _exportNewDestination;
            target.Text = SelectedExportDestination;
        }
        else if (!target.IsEnabled && !_exportRunning) target.Text = _exportNewDestination;
        UpdateExportActions();
    }
    private async void OnExportBrowse(object? sender, RoutedEventArgs e) => await PickExportFolderAsync(false);
    private async void OnExportAssociate(object? sender, RoutedEventArgs e) => await PickExportFolderAsync(true);
    private async Task PickExportFolderAsync(bool associate)
    {
        if (_exportRunning || _exportPicking || _exportLoading || _ended || _exportThread == null) return;
        ResourceId id = _exportThread; int request = _exportRequest;
        _exportPicking = true; UpdateExportActions();
        try
        {
            IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null || !storage.CanPickFolder) { ExportStatus(Texts.T("settings.pickerUnavailable")); return; }
            _exportPickerOpen = true;
            IReadOnlyList<IStorageFolder> folders;
            try { folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Texts.T(associate ? "export.associate" : "archive.exportBrowse"), AllowMultiple = false }); }
            finally { _exportPickerOpen = false; }
            try
            {
                if (_ended || request != _exportRequest || folders.Count == 0) return;
                string? parent = folders[0].TryGetLocalPath();
                if (parent == null) { ExportStatus(Texts.T("settings.pickerLocalOnly")); return; }
                await RememberExportDirectoryAsync(parent);
                if (associate && HasSelectedExportHistory)
                {
                    if (IsCollectionExport) await Task.Run(() => CollectionExportHistory!.AssociateAsync(parent, _exportMembers!, _lifetime.Token));
                    else await Task.Run(() => ExportHistory!.AssociateAsync(parent, id, _lifetime.Token));
                    await ReloadSelectedExportHistoryAsync(id, request);
                }
                else { _exportNewDestination = await Task.Run(() => FreshSelectedExportDestination(parent, id)); Control<TextBox>("ArchiveExportDestination").Text = _exportNewDestination; }
            }
            finally { foreach (IStorageFolder folder in folders) folder.Dispose(); }
        }
        catch (Exception exception) { if (!_ended) ExportStatus(exception.Message); }
        finally { _exportPicking = false; if (!_ended) UpdateExportActions(); }
    }
    private async void OnExportStart(object? sender, RoutedEventArgs e)
    {
        if (_exportRunning || _exportPicking || _exportLoading || _ended || _exportThread == null || _openReader == null) return;
        if (IsCollectionExport) { await StartCollectionExportAsync(); return; }
        ResourceId id = _exportThread;
        string destination = Control<TextBox>("ArchiveExportDestination").Text ?? string.Empty;
        ArchiveHtmlExportOptions options = new(Control<CheckBox>("ArchiveExportMedia").IsChecked == true);
        bool updating = UpdatingExport;
        PreviousExport? previous = SelectedExport;
        bool preserve = Control<CheckBox>("ArchiveExportBackup").IsChecked == true;
        if (updating && (previous?.Inspection == null || !preserve)) { ExportStatus(Texts.T("export.confirm")); return; }
        _exportRunning = true; LastExport = null;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportCancellation = cancellation; UpdateExportActions(); UpdateActions(); ExportStatus(Texts.T("archive.exportWorking"));
        try
        {
            await using ArchiveReader reader = await Task.Run(() => _openReader(cancellation.Token));
            Progress<ArchiveHtmlExportProgress> progress = new(value =>
            {
                if (!_ended && ReferenceEquals(_exportCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    ExportStatus(Texts.T(value.Phase == ArchiveHtmlExportPhase.ReadingMedia ? "archive.exportReading" : "archive.exportProgress", value.PostsWritten, value.FilesCopied));
            });
            ArchiveHtmlExportResult result = await Task.Run(() => updating
                ? ArchiveHtmlExporter.UpdateAsync(reader, id, destination, previous!.Inspection!.Metadata.ExportId, previous.Inspection.Metadata.Generation, options, preserve, progress, cancellation.Token, expectedObservedFingerprint: previous.Inspection.ObservedFingerprint)
                : ArchiveHtmlExporter.ExportAsync(reader, id, destination, options, progress, cancellation.Token));
            if (_ended) return;
            LastExport = result;
            string status = Texts.T("archive.exportDone", result.PostCount, result.FileCount, result.Omissions.Count, result.Warnings.Count) + "\n" + result.Directory;
            if (result.BackupDirectory != null) status += "\n" + Texts.T("export.backup", result.BackupDirectory);
            ExportStatus(status);
            if (ExportHistory != null)
            {
                try
                {
                    ArchiveExportOperationResult recorded = await Task.Run(() => ExportHistory.RecordPublishedAsync(result));
                    if (!recorded.HistoryRecorded) ExportStatus(status + "\n" + Texts.T("export.historyError", recorded.HistoryError));
                    else await ReloadExportHistoryAsync(id, _exportRequest);
                }
                catch (Exception exception) { ExportStatus(status + "\n" + Texts.T("export.historyError", exception.Message)); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!_ended) ExportStatus(Texts.T("archive.exportCancelled")); }
        catch (Exception exception)
        {
            if (!_ended)
            {
                ExportStatus(Texts.T("export.failed") + "\n" + exception.Message);
                // An unsuccessful replacement can leave the old generation unavailable or
                // restored elsewhere. Never keep offering the pre-attempt inspection.
                if (updating)
                {
                    ComboBox history = Control<ComboBox>("ArchiveExportHistory");
                    if (history.ItemsSource is PreviousExport[] choices)
                    {
                        int selected = history.SelectedIndex;
                        history.ItemsSource = choices.Select(item => item with { Inspection = null }).ToArray();
                        history.SelectedIndex = selected;
                    }
                    await ReloadExportHistoryAsync(id, _exportRequest);
                }
            }
        }
        finally { _exportCancellation = null; _exportRunning = false; if (!_ended) { UpdateExportActions(); UpdateActions(); } }
    }
    private void UpdateExportActions()
    {
        if (_exportCard == null) return;
        bool ready = !_exportRunning && !_exportPicking && !_exportLoading && _exportThread != null;
        Control<Button>("ArchiveExportStart").IsEnabled = ready;
        Control<Button>("ArchiveExportBrowse").IsEnabled = ready && !UpdatingExport;
        Control<Button>("ArchiveExportAssociate").IsEnabled = ready && HasSelectedExportHistory;
        Control<Button>("ArchiveExportLegacyFind").IsEnabled = ready;
        Control<Button>("ArchiveExportLegacyAssociate").IsEnabled = ready && Control<ComboBox>("ArchiveExportLegacy").SelectedItem != null && ExportHistory != null;
        Control<ComboBox>("ArchiveExportLegacy").IsEnabled = ready;
        Control<Button>("ArchiveExportPrevious").IsEnabled = ready && HasSelectedExportInspection;
        bool known = SelectedExportDestination != null;
        Control<ComboBox>("ArchiveExportHistory").IsVisible = known;
        Control<TextBlock>("ArchiveExportHistoryLabel").IsVisible = known;
        Control<Button>("ArchiveExportPrevious").IsVisible = known;
        Control<RadioButton>("ArchiveExportNew").IsVisible = known;
        Control<RadioButton>("ArchiveExportUpdate").IsVisible = known;
        Control<ComboBox>("ArchiveExportHistory").IsEnabled = ready;
        Control<RadioButton>("ArchiveExportNew").IsEnabled = ready;
        Control<RadioButton>("ArchiveExportUpdate").IsEnabled = ready && HasSelectedExportInspection;
        Control<TextBox>("ArchiveExportDestination").IsEnabled = ready && !UpdatingExport;
        Control<CheckBox>("ArchiveExportMedia").IsEnabled = ready;
        Control<CheckBox>("ArchiveExportBackup").IsEnabled = ready;
        Control<CheckBox>("ArchiveExportBackup").IsVisible = UpdatingExport;
        Control<TextBlock>("ArchiveExportBackupHint").IsVisible = UpdatingExport;
        Control<Button>("ArchiveExportOpen").IsEnabled = ready && PublishedExportDirectory != null;
        Control<Expander>("ArchiveExportLegacySection").IsVisible = !IsCollectionExport;
        LocalizeExport();
    }
    private void ExportStatus(string message) => Control<TextBlock>("ArchiveExportStatus").Text = message;
    private void OnExportClose(object? sender, RoutedEventArgs e)
    {
        if (_exportRunning) { _exportCancellation?.Cancel(); ExportStatus(Texts.T("archive.exportCancelling")); return; }
        if (_exportPicking)
        {
            if (!_exportPickerOpen) { _exportLookupCancellation?.Cancel(); ExportStatus(Texts.T("archive.exportCancelling")); }
            return;
        }
        _exportLookupCancellation?.Cancel();
        ++_exportRequest; _exportLoading = false; _exportResolving = false; Control<Border>("ArchiveExportDialog").IsVisible = false;
        EndExportDialog();
        UpdateActions();
        Action? closed = _exportClosed; _exportClosed = null;
        if (closed != null) closed(); else _exportOrigin?.Focus();
    }
    private async void OnExportOpen(object? sender, RoutedEventArgs e)
    { if (!_exportRunning && !_exportPicking && !_exportLoading && PublishedExportDirectory is string destination) await TryOpenExportFolderAsync(destination); }
    private async void OnExportPrevious(object? sender, RoutedEventArgs e)
    {
        if (IsCollectionExport) { await OpenPreviousCollectionExportAsync(); return; }
        if (_exportRunning || _exportPicking || SelectedExport == null || _exportThread == null) return;
        PreviousExport previous = SelectedExport;
        if (previous.Inspection == null) return;
        ArchiveExportInspection expected = previous.Inspection;
        string destination = previous.Destination; ResourceId id = _exportThread;
        int request = _exportRequest;
        _exportPicking = true; UpdateExportActions();
        try
        {
            ArchiveExportInspection current = await Task.Run(() => ArchiveHtmlExporter.InspectAsync(destination, id, _lifetime.Token));
            if (_ended || request != _exportRequest) return;
            if (current.Metadata.ExportId != expected.Metadata.ExportId || current.Metadata.Generation != expected.Metadata.Generation)
                throw new IOException(Texts.T("export.changed"));
            await TryOpenExportFolderAsync(destination);
        }
        catch (Exception exception) { ExportStatus(Texts.T("export.openFailed", exception.Message)); }
        finally { _exportPicking = false; if (!_ended) UpdateExportActions(); }
    }
    private async void OnExportLegacyFind(object? sender, RoutedEventArgs e)
    {
        if (_exportRunning || _exportPicking || _exportThread == null) return;
        ResourceId id = _exportThread; int request = _exportRequest;
        string target = Control<TextBox>("ArchiveExportDestination").Text ?? string.Empty;
        // Escape cancels a search without closing the modal. Each retry therefore
        // needs its own live token, rather than reusing the cancelled lookup.
        _exportLookupCancellation?.Dispose();
        _exportLookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportPicking = true; UpdateExportActions();
        try
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(target))!;
            CancellationToken lookup = _exportLookupCancellation?.Token ?? _lifetime.Token;
            var candidates = await Task.Run(() => ArchiveExportLegacy.FindCandidates(parent, id, lookup));
            if (_ended || request != _exportRequest) return;
            Control<ComboBox>("ArchiveExportLegacy").ItemsSource = candidates.Select(candidate => candidate.Directory).ToArray();
            Control<ComboBox>("ArchiveExportLegacy").SelectedIndex = candidates.Count > 0 ? 0 : -1;
            ExportStatus(Texts.T("export.legacyFound", candidates.Count));
        }
        catch (OperationCanceledException) when (_exportLookupCancellation?.IsCancellationRequested == true)
        { if (!_ended) ExportStatus(Texts.T("archive.exportCancelled")); }
        catch (Exception exception) { if (!_ended) ExportStatus(exception.Message); }
        finally { _exportPicking = false; if (!_ended) UpdateExportActions(); }
    }
    private async void OnExportLegacyAssociate(object? sender, RoutedEventArgs e)
    {
        if (_exportRunning || _exportPicking || _exportThread == null || ExportHistory == null || _openReader == null ||
            Control<ComboBox>("ArchiveExportLegacy").SelectedItem is not string destination) return;
        ResourceId id = _exportThread; int request = _exportRequest;
        _exportPicking = true; UpdateExportActions();
        try
        {
            await using ArchiveReader reader = await Task.Run(() => _openReader(_lifetime.Token));
            await Task.Run(() => ArchiveExportLegacy.AssociateAsync(reader, id, destination, _lifetime.Token));
            await Task.Run(() => ExportHistory.AssociateAsync(destination, id, _lifetime.Token));
            await ReloadSelectedExportHistoryAsync(id, request);
            ExportStatus(Texts.T("export.legacyAssociated"));
        }
        catch (Exception exception) { if (!_ended) ExportStatus(exception.Message); }
        finally { _exportPicking = false; if (!_ended) UpdateExportActions(); }
    }
    private async Task TryOpenExportFolderAsync(string destination)
    {
        _exportPicking = true; UpdateExportActions();
        try
        {
            if (OpenExportFolder != null) await OpenExportFolder(destination);
            else _openFolder?.Invoke(destination);
        }
        catch (Exception exception) { if (!_ended) ExportStatus(Texts.T("export.openFailed", exception.Message)); }
        finally { _exportPicking = false; if (!_ended) UpdateExportActions(); }
    }
    private void OnExportKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; OnExportClose(sender, e); } }
}
