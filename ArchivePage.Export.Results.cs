using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private bool _exportResolving;
    private bool _exportAllResults;

    private void UpdateResultsExportAction(bool ready)
    {
        Button button = Control<Button>("ArchiveResultsExport");
        string label = Texts.T("export.resultsAction", _listTotal);
        button.IsEnabled = ready && _openReader != null && _listTotal > 0
            && !Control<Border>("ArchiveExportDialog").IsVisible;
        button.Content = Texts.T("export.resultsShortAction");
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, Texts.T("export.resultsScope"));
    }

    private void OnResultsExportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !button.IsEnabled) return;
        // The explicit menu names the search scope; selection-export keeps its
        // own command and never inherits the complete result set.
        MenuItem item = CommandBindings.CreateMenuItem("export.open-all-results-html-dialog",
            async () => await OpenExportForAllResultsAsync(button));
        string label = Texts.T("export.resultsAction", _listTotal);
        item.Header = label;
        AutomationProperties.SetName(item, label);
        ToolTip.SetTip(item, Texts.T("export.resultsScope"));
        AutomationProperties.SetHelpText(item, Texts.T("export.resultsScope"));
        button.ContextMenu = new ContextMenu { Items = { item } };
        button.ContextMenu.Open(button);
    }

    internal async Task OpenExportForAllResultsAsync(Control? origin)
    {
        if (_ended || !_available || _busy || _openReader == null) return;
        EnsureExportDialog();
        if (Control<Border>("ArchiveExportDialog").IsVisible)
        { Control<Button>("ArchiveExportClose").Focus(); return; }

        // Freeze applied inputs before yielding. Work membership is evaluated now,
        // independently of the catalog read; this is not a session/catalog transaction.
        ArchiveThreadQuery query = _query;
        IReadOnlySet<string>? eligibleUrls = CaptureExportWorkUrls(_workFilter,
            GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>());
        int request = ++_exportRequest;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportLookupCancellation?.Cancel();
        _exportLookupCancellation?.Dispose();
        _exportLookupCancellation = cancellation;
        _exportThread = null; _exportMembers = null; _exportClosed = null;
        _exportOrigin = origin; _exportAllResults = true; _exportResolving = true; _exportLoading = true;
        LastExport = null; LastCollectionExport = null;
        Control<ComboBox>("ArchiveExportHistory").ItemsSource = Array.Empty<PreviousExport>();
        Control<ComboBox>("ArchiveExportLegacy").ItemsSource = Array.Empty<string>();
        Control<RadioButton>("ArchiveExportNew").IsChecked = true;
        Control<TextBox>("ArchiveExportDestination").Text = string.Empty;
        ExportStatus(string.Empty);
        ShowExportDialog();
        UpdateExportActions();
        UpdateActions();
        Control<Button>("ArchiveExportClose").Focus();
        try
        {
            await using ArchiveReader reader = await _openReader(cancellation.Token);
            ResourceId[] members = await ResolveExportResultIdsAsync(reader, query, eligibleUrls, cancellation.Token);
            if (_ended || cancellation.IsCancellationRequested || request != _exportRequest
                || !Control<Border>("ArchiveExportDialog").IsVisible) return;
            if (members.Length == 0)
            { ExportStatus(Texts.T("export.resultsEmpty")); return; }
            // The existing exporter snapshots each target's content after Start.
            // No later query, new match, or work-state change may rewrite these IDs.
            await OpenExportCoreAsync(members[0], origin, members.Length > 1 ? members : null,
                null, allResults: true, resolvedResults: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_ended && request == _exportRequest)
                ExportStatus(Texts.T("export.resultsFailed", exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_exportLookupCancellation, cancellation)) _exportLookupCancellation = null;
            if (!_ended && request == _exportRequest)
            {
                _exportResolving = false; _exportLoading = false;
                UpdateExportActions(); UpdateActions();
            }
        }
    }

    internal static IReadOnlySet<string>? CaptureExportWorkUrls(int workFilter, IReadOnlyList<ThreadWorkSnapshot> work)
    {
        if (workFilter == 0) return null;
        return work.GroupBy(item => item.Url, StringComparer.Ordinal).Select(group => group.First())
            .Where(item => WorkMatches(item, workFilter)).Select(item => item.Url).ToHashSet(StringComparer.Ordinal);
    }

    internal static async Task<ResourceId[]> ResolveExportResultIdsAsync(ArchiveReader reader,
        ArchiveThreadQuery query, IReadOnlySet<string>? eligibleUrls, CancellationToken token)
    {
        if (eligibleUrls == null)
            return (await reader.SelectThreadIdsAsync(query, token)).Distinct().ToArray();
        IReadOnlyList<ArchiveThreadSummary> summaries = await reader.SelectThreadSummariesAsync(query, token);
        return summaries.Where(item => eligibleUrls.Contains(item.Url)).Select(item => item.Id).Distinct().ToArray();
    }
}
