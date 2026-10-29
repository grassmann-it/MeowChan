using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class DiagnosticsPage
{
    private IReadOnlyList<DiagnosticEntry> _visibleDiagnostics = Array.Empty<DiagnosticEntry>();
    private string? _diagnosticSourceId;
    private bool _refreshingDiagnosticFilters;
    private bool _diagnosticFiltersReady;
    internal Func<DiagnosticTarget, Task<int>>? RetryMediaAsync { get; set; }
    internal Func<DiagnosticTarget, bool>? CanRetryMedia { get; set; }

    private DiagnosticEntry[] SelectedDiagnostics()
    {
        HashSet<string> keys = this.FindControl<DataGrid>("DiagnosticsGrid")!.SelectedItems
            .OfType<DiagnosticEntry>().Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        return _visibleDiagnostics.Where(entry => keys.Contains(entry.Key)).ToArray();
    }

    private void RefreshDiagnosticSources()
    {
        ComboBox sources = this.FindControl<ComboBox>("DiagnosticSourceFilter")!;
        string[] choices = new[] { Texts.T("diagnostics.allSources") }.Concat(_entries
            .Select(entry => entry.ResourceId?.Source).Where(id => id != null).Cast<string>()
            .Concat(_diagnosticSourceId == null ? Array.Empty<string>() : new[] { _diagnosticSourceId })
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)).ToArray();
        if (sources.ItemsSource?.Cast<string>().SequenceEqual(choices) == true) return;
        _refreshingDiagnosticFilters = true;
        try
        {
            sources.ItemsSource = choices;
            sources.SelectedIndex = _diagnosticSourceId == null ? 0 : Array.IndexOf(choices, _diagnosticSourceId);
        }
        finally { _refreshingDiagnosticFilters = false; }
    }

    private void OnDiagnosticFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_diagnosticFiltersReady || _refreshingDiagnosticFilters) return;
        if (ReferenceEquals(sender, this.FindControl<ComboBox>("DiagnosticSourceFilter")))
        {
            ComboBox sources = (ComboBox)sender!;
            _diagnosticSourceId = sources.SelectedIndex > 0 ? sources.SelectedItem as string : null;
        }
        ApplyDiagnosticFilters();
        RefreshSelection();
    }

    private void OnClearDiagnosticFilters(object? sender, RoutedEventArgs e)
    {
        _refreshingDiagnosticFilters = true;
        try
        {
            _diagnosticSourceId = null;
            this.FindControl<TextBox>("DiagnosticTextFilter")!.Text = string.Empty;
            this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.IsChecked = false;
            this.FindControl<ComboBox>("DiagnosticSourceFilter")!.SelectedIndex = 0;
        }
        finally { _refreshingDiagnosticFilters = false; }
        ApplyDiagnosticFilters(); RefreshSelection();
    }

    private void ApplyDiagnosticFilters()
    {
        DataGrid grid = this.FindControl<DataGrid>("DiagnosticsGrid")!;
        string? query = this.FindControl<TextBox>("DiagnosticTextFilter")!.Text;
        bool problems = this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.IsChecked == true;
        DiagnosticEntry[] visible = _entries.Where(entry => DiagnosticRetryScope.Matches(entry, _diagnosticSourceId, query, problems)).ToArray();
        if (!_visibleDiagnostics.SequenceEqual(visible))
        {
            string? lead = (grid.SelectedItem as DiagnosticEntry)?.Key;
            HashSet<string> selected = grid.SelectedItems.OfType<DiagnosticEntry>().Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            DiagnosticEntry[] retained = visible.Where(entry => selected.Contains(entry.Key)).ToArray();
            if (selected.Count > retained.Length) _diagnosticSelectionRemoved = true;
            _refreshingDiagnostics = true;
            try
            {
                _visibleDiagnostics = visible;
                grid.ItemsSource = visible;
                grid.SelectedItem = retained.FirstOrDefault(entry => entry.Key == lead) ?? retained.FirstOrDefault();
                foreach (DiagnosticEntry item in retained)
                    if (!grid.SelectedItems.Contains(item)) grid.SelectedItems.Add(item);
            }
            finally { _refreshingDiagnostics = false; }
        }
        this.FindControl<TextBlock>("DiagnosticsEmpty")!.IsVisible = visible.Length == 0;
    }

    private void RefreshDiagnosticFilterLabels()
    {
        ComboBox sources = this.FindControl<ComboBox>("DiagnosticSourceFilter")!;
        TextBox text = this.FindControl<TextBox>("DiagnosticTextFilter")!;
        AutomationProperties.SetName(sources, Texts.T("sources.title"));
        AutomationProperties.SetName(text, Texts.T("diagnostics.filterText"));
        text.Watermark = Texts.T("diagnostics.filterText");
        this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.Content = Texts.T("diagnostics.onlyProblems");
        this.FindControl<Button>("DiagnosticClearFilters")!.Content = Texts.T("diagnostics.clearFilters");
    }

    private bool CanRetryScope(IReadOnlyList<DiagnosticRetryTarget> scope) => !_ended && _available && !_retryBusy
        && scope.Any(item => item.Check && _retry != null && _canRetry?.Invoke(item.Target) == true
            || item.Media && RetryMediaAsync != null && CanRetryMedia?.Invoke(item.Target) == true);

    private void RefreshDiagnosticBatchControls()
    {
        DiagnosticEntry[] selected = SelectedDiagnostics();
        DiagnosticRetryTarget[] selection = DiagnosticRetryScope.Capture(selected);
        DiagnosticRetryTarget[] filtered = DiagnosticRetryScope.Capture(_visibleDiagnostics);
        foreach (var (name, command, scope) in new[] {
            ("RetryDiagnosticSelectionButton", "diagnostics.retry-selection", selection),
            ("RetryDiagnosticFilteredButton", "diagnostics.retry-filtered", filtered) })
        {
            Button button = this.FindControl<Button>(name)!;
            CommandBindings.ApplyLabeled(button, command, CommandCatalog.Label(command) + " (" + scope.Length + ")");
            button.IsEnabled = CanRetryScope(scope);
            ToolTip.SetTip(button, Texts.T("diagnostics.retryScope"));
            AutomationProperties.SetHelpText(button, Texts.T("diagnostics.retryScope"));
        }
        this.FindControl<TextBlock>("DiagnosticFilterSummary")!.Text = Texts.T("diagnostics.filterCount",
            _visibleDiagnostics.Count, _entries.Count, selected.Length, selection.Length);
    }

    private async void OnRetryDiagnosticSelection(object? sender, RoutedEventArgs e)
        => await RetryFrozenScopeAsync(DiagnosticRetryScope.Capture(SelectedDiagnostics()));
    private async void OnRetryDiagnosticFiltered(object? sender, RoutedEventArgs e)
        => await RetryFrozenScopeAsync(DiagnosticRetryScope.Capture(_visibleDiagnostics));

    private bool StillHasProblem(DiagnosticTarget target, bool media) => _entries.Any(entry => entry.Target == target
        && entry.IsProblem && (media ? entry.Kind == DiagnosticKind.Media : entry.Kind != DiagnosticKind.Media || entry.CheckAlso));

    private async Task RetryFrozenScopeAsync(DiagnosticRetryTarget[] scope)
    {
        if (!CanRetryScope(scope)) return;
        int generation = BeginDiagnosticAction();
        _retryBusy = true; RefreshSelection();
        int checks = 0, already = 0, files = 0, skipped = 0, failures = 0;
        List<string> errors = new();
        void Failed(DiagnosticTarget target, Exception error)
        {
            failures++;
            if (errors.Count < 5) errors.Add(target.Kind + " #" + target.WatchId + ": "
                + error.Message[..Math.Min(300, error.Message.Length)]);
        }
        try
        {
            // Deliberately serial and bounded by the captured scope. No background retry
            // loops, Task.WhenAll fan-out, unpause or source activation is introduced.
            foreach (DiagnosticRetryTarget item in scope)
            {
                if (_ended) break;
                if (item.Media)
                {
                    if (!StillHasProblem(item.Target, media: true) || RetryMediaAsync == null
                        || CanRetryMedia?.Invoke(item.Target) != true) skipped++;
                    else try
                    {
                        int accepted = await RetryMediaAsync(item.Target);
                        files += accepted;
                        if (accepted == 0) skipped++;
                    }
                    catch (Exception error) { Failed(item.Target, error); }
                }
                if (_ended) break;
                if (item.Check)
                {
                    if (!StillHasProblem(item.Target, media: false) || _retry == null
                        || _canRetry?.Invoke(item.Target) != true) { skipped++; continue; }
                    try
                    {
                        CheckRequestStatus result = await _retry(item.Target);
                        if (result == CheckRequestStatus.Scheduled) checks++;
                        else if (result == CheckRequestStatus.AlreadyScheduled) already++;
                        else skipped++;
                    }
                    catch (Exception error) { Failed(item.Target, error); }
                }
            }
        }
        finally { _retryBusy = false; if (!_ended) RefreshSelection(); }
        if (!_ended) ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Global),
            Texts.T("diagnostics.batchResult", scope.Length, checks, already, files, skipped, failures)
                + (errors.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, errors)), failures > 0);
    }
}
