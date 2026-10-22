using System;
using System.Collections.Generic;
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
    internal Task OpenExportForThreadsAsync(IReadOnlyList<ResourceId> frozenIds, Control? origin, Action? onClosed = null)
    {
        ResourceId[] members = FreezeExportMembers(frozenIds);
        return OpenExportCoreAsync(members[0], origin, members.Length > 1 ? members : null, onClosed);
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
        string stem = Path.Combine(parent, "meowchan-collection-" + count + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string candidate = stem;
        for (int suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++) candidate = stem + "-" + suffix;
        return candidate;
    }
    private Task ReloadSelectedExportHistoryAsync(ResourceId id, int request) => IsCollectionExport
        ? ReloadCollectionExportHistoryAsync(request) : ReloadExportHistoryAsync(id, request);
    private async Task ReloadCollectionExportHistoryAsync(int request)
    {
        if (CollectionExportHistory == null || _exportMembers == null) return;
        string? selectedDestination = SelectedCollectionExport?.Destination;
        ResourceId[] members = _exportMembers;
        try
        {
            CancellationToken lookup = _exportLookupCancellation?.Token ?? _lifetime.Token;
            var entries = await Task.Run(() => CollectionExportHistory.ListVerifiedAsync(members, lookup));
            if (_ended || request != _exportRequest || !Control<Border>("ArchiveExportDialog").IsVisible) return;
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
            if (!_ended && request == _exportRequest)
                ExportStatus(Control<TextBlock>("ArchiveExportStatus").Text + "\n" + Texts.T("export.historyError", exception.Message));
        }
    }
    private async Task StartCollectionExportAsync()
    {
        ResourceId[] members = _exportMembers!;
        string destination = Control<TextBox>("ArchiveExportDestination").Text ?? string.Empty;
        ArchiveHtmlExportOptions options = new(Control<CheckBox>("ArchiveExportMedia").IsChecked == true);
        bool updating = UpdatingExport;
        PreviousCollectionExport? previous = SelectedCollectionExport;
        bool preserve = Control<CheckBox>("ArchiveExportBackup").IsChecked == true;
        if (updating && (previous?.Inspection == null || !preserve)) { ExportStatus(Texts.T("export.confirm")); return; }
        _exportRunning = true; LastCollectionExport = null; LastExport = null;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportCancellation = cancellation; UpdateExportActions(); UpdateActions(); ExportStatus(Texts.T("archive.exportWorking"));
        try
        {
            await using ArchiveReader reader = await Task.Run(() => _openReader!(cancellation.Token));
            Progress<ArchiveCollectionHtmlExportProgress> progress = new(value =>
            {
                if (!_ended && ReferenceEquals(_exportCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    ExportStatus(Texts.T("export.collectionProgress", value.ThreadsCompleted, value.ThreadCount, value.PostsWritten, value.FilesCopied));
            });
            ArchiveCollectionHtmlExportResult result = await Task.Run(() => updating
                ? ArchiveCollectionHtmlExporter.UpdateAsync(reader, members, destination, previous!.Inspection!.Metadata.ExportId,
                    previous.Inspection.Metadata.Generation, previous.Inspection.ObservedFingerprint, options, preserve, progress, cancellation.Token)
                : ArchiveCollectionHtmlExporter.ExportAsync(reader, members, destination, options, progress, cancellation.Token));
            if (_ended) return;
            LastCollectionExport = result;
            string status = CollectionExportResultText(result);
            ExportStatus(status);
            if (CollectionExportHistory != null)
            {
                try
                {
                    ArchiveCollectionExportOperationResult recorded = await Task.Run(() => CollectionExportHistory.RecordPublishedAsync(result));
                    if (!recorded.HistoryRecorded) ExportStatus(status + "\n" + Texts.T("export.historyError", recorded.HistoryError));
                    else await ReloadCollectionExportHistoryAsync(_exportRequest);
                }
                catch (Exception exception) { ExportStatus(status + "\n" + Texts.T("export.historyError", exception.Message)); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_ended) ExportStatus(Texts.T("archive.exportCancelled")); }
        catch (Exception exception)
        {
            if (!_ended)
            {
                ExportStatus(Texts.T("export.failed") + "\n" + exception.Message);
                if (updating)
                {
                    ComboBox history = Control<ComboBox>("ArchiveExportHistory");
                    if (history.ItemsSource is PreviousCollectionExport[] choices)
                    {
                        int selected = history.SelectedIndex;
                        history.ItemsSource = choices.Select(item => item with { Inspection = null }).ToArray();
                        history.SelectedIndex = selected;
                    }
                    await ReloadCollectionExportHistoryAsync(_exportRequest);
                }
            }
        }
        finally { _exportCancellation = null; _exportRunning = false; if (!_ended) { UpdateExportActions(); UpdateActions(); } }
    }
    internal static string CollectionExportResultText(ArchiveCollectionHtmlExportResult result)
    {
        List<string> lines = new()
        {
            Texts.T("export.collectionDone", result.ThreadCount, result.PostCount, result.FileCount, result.Omissions.Count, result.Warnings.Count),
            result.Directory
        };
        if (result.Metadata != null)
            foreach (ArchiveCollectionMember member in result.Metadata.Members)
                lines.Add(Texts.T("export.collectionMemberDone", ExportMemberLabel(member.Thread), member.PostCount,
                    result.Omissions.Count(item => item.Thread == member.Thread)));
        foreach (var omissions in result.Omissions.GroupBy(item => (item.Thread, item.Reason)))
            lines.Add(ExportMemberLabel(omissions.Key.Thread) + ": " + Texts.T("export.omission." + omissions.Key.Reason) + " × " + omissions.Count());
        lines.AddRange(result.Warnings);
        if (result.BackupDirectory != null) lines.Add(Texts.T("export.backup", result.BackupDirectory));
        return string.Join("\n", lines);
    }
    private async Task OpenPreviousCollectionExportAsync()
    {
        if (_exportRunning || _exportPicking || _exportLoading || SelectedCollectionExport?.Inspection == null || _exportMembers == null) return;
        PreviousCollectionExport previous = SelectedCollectionExport;
        ArchiveCollectionExportInspection expected = previous.Inspection!;
        ResourceId[] members = _exportMembers;
        int request = _exportRequest;
        _exportPicking = true; UpdateExportActions();
        try
        {
            ArchiveCollectionExportInspection current = await Task.Run(() => ArchiveCollectionHtmlExporter.InspectAsync(previous.Destination, members, _lifetime.Token));
            if (_ended || request != _exportRequest) return;
            if (current.Metadata.ExportId != expected.Metadata.ExportId || current.Metadata.Generation != expected.Metadata.Generation ||
                current.ObservedFingerprint != expected.ObservedFingerprint) throw new IOException(Texts.T("export.changed"));
            await TryOpenExportFolderAsync(current.Directory);
        }
        catch (Exception exception) { if (!_ended) ExportStatus(Texts.T("export.openFailed", exception.Message)); }
        finally { _exportPicking = false; if (!_ended) UpdateExportActions(); }
    }
}
