using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private ResourceId[]? _exportMembers;
    private Action? _exportClosed;
    internal CollectionExportHistoryStore? CollectionExportHistory { get; set; }
    internal ArchiveCollectionHtmlExportResult? LastCollectionExport { get; private set; }
    private bool IsCollectionExport => _exportMembers is { Length: > 1 };
    private bool HasSelectedExportHistory => IsCollectionExport ? CollectionExportHistory != null : ExportHistory != null;
    private string? PublishedExportDirectory => IsCollectionExport ? LastCollectionExport?.Directory : LastExport?.Directory;
    private PreviousCollectionExport? SelectedCollectionExport => Control<ComboBox>("ArchiveExportHistory").SelectedItem as PreviousCollectionExport;
    private string? SelectedExportDestination => IsCollectionExport ? SelectedCollectionExport?.Destination : SelectedExport?.Destination;
    private bool HasSelectedExportInspection => IsCollectionExport ? SelectedCollectionExport?.Inspection != null : SelectedExport?.Inspection != null;
    private sealed record PreviousCollectionExport(string Destination, DateTimeOffset Date, ArchiveCollectionExportInspection? Inspection)
    {
        public override string ToString() => Date.ToLocalTime().ToString("g", Texts.Culture) + " · " + Destination +
            (Inspection == null ? " · " + Texts.T("export.missing") : string.Empty);
    }

    // Copy the ordered identity set before showing the modal or awaiting any work.
    // offerResults: the entry sits on the archive results, so "all matches" is a further scope;
    // format preselects the dialog (a link list needs exactly one thread).
    internal Task OpenExportForThreadsAsync(IReadOnlyList<ResourceId> frozenIds, Control? origin, Action? onClosed = null,
        bool offerResults = false, ExportFormat format = ExportFormat.Html)
    {
        ResourceId[] members = FreezeExportMembers(frozenIds);
        return OpenExportCoreAsync(members[0], origin, members.Length > 1 ? members : null, onClosed,
            offerResults: offerResults, format: format);
    }
    internal static ResourceId[] FreezeExportMembers(IReadOnlyList<ResourceId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ResourceId[] members = ids.Distinct().ToArray();
        if (members.Length == 0 || members.Any(id => id == null)) throw new ArgumentException("Select at least one thread.", nameof(ids));
        return members;
    }
    private void UpdateExportSummary()
    {
        TextBlock compact = Control<TextBlock>("ArchiveExportScopeCompact");
        compact.IsVisible = !_exportAllResults || _exportThread == null || (_exportMembers?.Length ?? 1) == 1;
        compact.Text = _exportThread == null ? (_exportResolving ? Texts.T("export.resultsResolving") : string.Empty)
            : IsCollectionExport ? Texts.T("export.collectionSelection", _exportMembers!.Length) : ExportMemberLabel(_exportThread);
        Control<Expander>("ArchiveExportScopeDetails").IsVisible = (_exportMembers?.Length ?? 1) > 1;
        TextBlock fixedScope = Control<TextBlock>("ArchiveExportFixedScope");
        fixedScope.IsVisible = _exportAllResults && _exportThread != null;
        fixedScope.Text = fixedScope.IsVisible ? Texts.T("export.resultsFrozen", _exportMembers?.Length ?? 1) : string.Empty;
        if (_exportThread == null)
        {
            Control<TextBlock>("ArchiveExportSummary").Text = _exportResolving ? Texts.T("export.resultsResolving") : string.Empty;
            return;
        }
        if (_exportAllResults)
        {
            ResourceId[] members = _exportMembers ?? new[] { _exportThread };
            Control<TextBlock>("ArchiveExportSummary").Text = Texts.T("export.resultsFrozen", members.Length) + "\n" +
                string.Join("\n", members.Select(ExportMemberLabel));
            return;
        }
        Control<TextBlock>("ArchiveExportSummary").Text = IsCollectionExport
            ? Texts.T("export.collectionSelection", _exportMembers!.Length) + "\n" + string.Join("\n", _exportMembers.Select(ExportMemberLabel))
            : ExportMemberLabel(_exportThread);
    }
    private static string ExportMemberLabel(ResourceId id) => id.Source + " /" + id.Board + "/ " + id.Thread;
    private string FreshSelectedExportDestination(string parent, ResourceId id) => IsCollectionExport
        ? FreshCollectionExportDestination(parent, _exportMembers!.Length) : FreshExportDestination(parent, id);
    internal static string FreshCollectionExportDestination(string parent, int count)
    {
        string stem = Path.Combine(parent, "meowchan-collection-" + count + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string candidate = stem;
        for (int suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++) candidate = stem + "-" + suffix;
        return candidate;
    }
    private Task ReloadSelectedExportHistoryAsync(ResourceId id, int request) => IsCollectionExport
        ? ReloadCollectionExportHistoryAsync(request) : ReloadExportHistoryAsync(id, request);
    private async Task ReloadCollectionExportHistoryAsync(int request, CancellationTokenSource? operation = null)
    {
        bool Current() => ExportRequestIsCurrent(request) && (operation == null
            || ReferenceEquals(_exportCancellation, operation) && !operation.IsCancellationRequested);
        if (!Current() || CollectionExportHistory == null || _exportMembers == null) return;
        string? selectedDestination = SelectedCollectionExport?.Destination;
        ResourceId[] members = _exportMembers;
        try
        {
            CancellationToken lookup = _exportLookupCancellation?.Token ?? _lifetime.Token;
            var entries = await Task.Run(() => CollectionExportHistory.ListVerifiedAsync(members, lookup));
            if (!Current() || lookup.IsCancellationRequested || !ArchiveExportDialog.IsVisible) return;
            PreviousCollectionExport[] choices = entries.OrderBy(item => item.Inspection == null)
                .Select(item => new PreviousCollectionExport(item.Entry.Destination, item.Entry.ExportedAt, item.Inspection)).ToArray();
            ComboBox history = Control<ComboBox>("ArchiveExportHistory");
            history.ItemsSource = choices;
            int selected = Array.FindIndex(choices, item => item.Destination == selectedDestination);
            history.SelectedIndex = selected >= 0 ? selected : choices.Length > 0 ? 0 : -1;
            UpdateExportMode();
        }
        catch (Exception exception)
        {
            if (Current())
                ExportError(ArchiveExportStatusText.Localized("export.historyError", exception.Message));
        }
    }
    private async Task StartCollectionExportAsync()
    {
        int request = _exportRequest;
        if (!ExportRequestIsCurrent(request) || _exportRunning || _exportPicking || _exportLoading
            || _exportMembers == null || _openReader == null) return;
        ResourceId[] members = _exportMembers!;
        string destination = Control<TextBox>("ArchiveExportDestination").Text ?? string.Empty;
        ArchiveHtmlExportOptions options = new(Control<CheckBox>("ArchiveExportMedia").IsChecked == true)
        {
            Language = Texts.Language
        };
        bool updating = UpdatingExport;
        PreviousCollectionExport? previous = SelectedCollectionExport;
        bool preserve = Control<CheckBox>("ArchiveExportBackup").IsChecked == true;
        if (updating && (previous?.Inspection == null || !preserve)) { ExportStatus(ArchiveExportStatusText.Localized("export.confirm")); return; }
        _exportRunning = true; LastCollectionExport = null; LastExport = null;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportCancellation = cancellation; ExportError(string.Empty); UpdateExportActions(); UpdateActions(); ExportStatus(ArchiveExportStatusText.Localized("archive.exportWorking"));
        bool Current() => ExportRequestIsCurrent(request) && ReferenceEquals(_exportCancellation, cancellation)
            && !cancellation.IsCancellationRequested;
        try
        {
            await using ArchiveReader reader = await Task.Run(() => _openReader!(cancellation.Token));
            if (!Current()) return;
            Progress<ArchiveCollectionHtmlExportProgress> progress = new(value =>
            {
                if (Current())
                    ExportStatus(ArchiveExportStatusText.Localized("export.collectionProgress", value.ThreadsCompleted, value.ThreadCount, value.PostsWritten, value.FilesCopied));
            });
            ArchiveCollectionHtmlExportResult result = await Task.Run(() => updating
                ? ArchiveCollectionHtmlExporter.UpdateAsync(reader, members, destination, previous!.Inspection!.Metadata.ExportId,
                    previous.Inspection.Metadata.Generation, previous.Inspection.ObservedFingerprint, options, preserve, progress, cancellation.Token)
                : ArchiveCollectionHtmlExporter.ExportAsync(reader, members, destination, options, progress, cancellation.Token));
            if (!Current()) return;
            LastCollectionExport = result;
            ExportStatus(ArchiveExportStatusText.CollectionResult(result));
            MarkExportSucceeded();
            if (CollectionExportHistory != null)
            {
                try
                {
                    ArchiveCollectionExportOperationResult recorded = await Task.Run(() => CollectionExportHistory.RecordPublishedAsync(result));
                    if (!Current()) return;
                    if (!recorded.HistoryRecorded) ExportError(ArchiveExportStatusText.Localized("export.historyError", recorded.HistoryError));
                    else await ReloadCollectionExportHistoryAsync(request, cancellation);
                }
                catch (Exception exception) { if (Current()) ExportError(ArchiveExportStatusText.Localized("export.historyError", exception.Message)); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (ExportRequestIsCurrent(request)) ExportStatus(ArchiveExportStatusText.Localized("archive.exportCancelled")); }
        catch (Exception exception)
        {
            if (ExportRequestIsCurrent(request) && ReferenceEquals(_exportCancellation, cancellation))
            {
                if (cancellation.IsCancellationRequested) { ExportStatus(ArchiveExportStatusText.Localized("archive.exportCancelled")); return; }
                ExportError(ArchiveExportStatusText.Failed(exception.Message));
                if (updating)
                {
                    ComboBox history = Control<ComboBox>("ArchiveExportHistory");
                    if (history.ItemsSource is PreviousCollectionExport[] choices)
                    {
                        int selected = history.SelectedIndex;
                        history.ItemsSource = choices.Select(item => item with { Inspection = null }).ToArray();
                        history.SelectedIndex = selected;
                    }
                    await ReloadCollectionExportHistoryAsync(request, cancellation);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_exportCancellation, cancellation))
            {
                _exportCancellation = null; _exportRunning = false;
                if (!_ended) { UpdateExportActions(); UpdateActions(); }
            }
        }
    }
    internal static string CollectionExportResultText(ArchiveCollectionHtmlExportResult result)
        => ArchiveExportStatusText.CollectionResult(result).Render();

    private async Task OpenPreviousCollectionExportAsync()
    {
        if (!ExportRequestIsCurrent(_exportRequest) || _exportRunning || _exportPicking || _exportLoading
            || SelectedCollectionExport?.Inspection == null || _exportMembers == null) return;
        PreviousCollectionExport previous = SelectedCollectionExport;
        ArchiveCollectionExportInspection expected = previous.Inspection!;
        ResourceId[] members = _exportMembers;
        int request = _exportRequest;
        _exportPicking = true; UpdateExportActions();
        try
        {
            ArchiveCollectionExportInspection current = await Task.Run(() => ArchiveCollectionHtmlExporter.InspectAsync(previous.Destination, members, _lifetime.Token));
            if (!ExportRequestIsCurrent(request)) return;
            if (current.Metadata.ExportId != expected.Metadata.ExportId || current.Metadata.Generation != expected.Metadata.Generation ||
                current.ObservedFingerprint != expected.ObservedFingerprint)
            {
                ExportError(ArchiveExportStatusText.Localized("export.openFailed",
                    ArchiveExportStatusText.Localized("export.changed")));
                return;
            }
            await TryOpenExportFolderAsync(current.Directory);
        }
        catch (Exception exception) { if (ExportRequestIsCurrent(request)) ExportError(ArchiveExportStatusText.Localized("export.openFailed", exception.Message)); }
        finally { if (request == _exportRequest) { _exportPicking = false; if (!_ended) UpdateExportActions(); } }
    }
}
