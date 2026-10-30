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

    private sealed record DiagnosticSourceChoice(string? SourceId, string Label)
    {
        public override string ToString() => Label;
    }

    private void RefreshDiagnosticSources()
    {
        ComboBox sources = this.FindControl<ComboBox>("DiagnosticSourceFilter")!;
        DiagnosticSourceChoice[] choices = new[] { new DiagnosticSourceChoice(null, Texts.T("diagnostics.allSources")) }
            .Concat(_entries.Select(entry => entry.ResourceId?.Source).Where(id => id != null).Cast<string>()
                .Concat(_diagnosticSourceId == null ? Array.Empty<string>() : new[] { _diagnosticSourceId })
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => new DiagnosticSourceChoice(id, id))).ToArray();
        if (sources.ItemsSource?.Cast<DiagnosticSourceChoice>().SequenceEqual(choices) == true) return;
        _refreshingDiagnosticFilters = true;
        try
        {
            sources.ItemsSource = choices;
            sources.SelectedItem = choices.First(choice => choice.SourceId == _diagnosticSourceId);
        }
        finally { _refreshingDiagnosticFilters = false; }
    }

    private void OnDiagnosticFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_diagnosticFiltersReady || _refreshingDiagnosticFilters) return;
        if (ReferenceEquals(sender, this.FindControl<ComboBox>("DiagnosticSourceFilter")))
        {
            ComboBox sources = (ComboBox)sender!;
            _diagnosticSourceId = (sources.SelectedItem as DiagnosticSourceChoice)?.SourceId;
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
        text.PlaceholderText = Texts.T("diagnostics.filterText");
        this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.Content = Texts.T("diagnostics.onlyProblems");
        this.FindControl<Button>("DiagnosticClearFilters")!.Content = Texts.T("diagnostics.clearFilters");
    }

    private bool CanRetryScope(IReadOnlyList<DiagnosticRetryTarget> scope) => !_ended && _available && !_retryBusy
        && scope.Any(item => item.Check && CanSubmitFrozen(item, media: false)
            || item.Media && CanSubmitFrozen(item, media: true));

    private bool CanSubmitFrozen(DiagnosticRetryTarget item, bool media)
    {
        if (_ended || !_available || !StillHasProblem(item, media)) return false;
        bool admitted = media ? RetryMediaAsync != null && CanRetryMedia?.Invoke(item.Target) == true
            : _retry != null && _canRetry?.Invoke(item.Target) == true;
        // An advisory callback can itself refresh/end the owner synchronously.
        return admitted && !_ended && _available && StillHasProblem(item, media);
    }

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

    private bool StillHasProblem(DiagnosticRetryTarget item, bool media) => _entries.Any(entry => entry.Target == item.Target
        && entry.ResourceId == item.ResourceId && entry.IsProblem
        && (media ? entry.Kind == DiagnosticKind.Media : DiagnosticRetryScope.IsCheckProblem(entry)));

    private async Task RetryFrozenScopeAsync(DiagnosticRetryTarget[] scope)
    {
        if (_ended || !_available || _retryBusy || scope.Length == 0) return;
        // Take the busy lease before refreshing controls or invoking callbacks.
        _retryBusy = true;
        int generation = BeginDiagnosticAction();
        try
        {
            RefreshSelection();
            DiagnosticRetryResult result = await DiagnosticRetryScope.ExecuteAsync(scope,
                CanSubmitFrozen, _retry, RetryMediaAsync);
            if (!_ended) ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Global),
                Texts.T("diagnostics.batchResult", result.Targets, result.Checks, result.AlreadyScheduled,
                    result.Files, result.Skipped, result.Failures)
                    + (result.Errors.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, result.Errors)),
                result.Failures > 0);
        }
        finally { _retryBusy = false; if (!_ended) RefreshSelection(); }
    }
}
