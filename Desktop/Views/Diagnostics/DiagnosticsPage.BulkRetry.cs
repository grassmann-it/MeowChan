using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class DiagnosticsPage
{
    // The table holds requests and problems; the platform facts (Information) are listed in their own group.
    private IReadOnlyList<DiagnosticEntry> _visibleDiagnostics = Array.Empty<DiagnosticEntry>();
    private IReadOnlyList<DiagnosticEntry> _visiblePlatform = Array.Empty<DiagnosticEntry>();
    private string? _diagnosticSourceId;
    private bool _refreshingDiagnosticFilters;
    private bool _diagnosticFiltersReady;
    internal Func<DiagnosticTarget, Task<int>>? RetryMediaAsync { get; set; }
    internal Func<DiagnosticTarget, bool>? CanRetryMedia { get; set; }

    private DiagnosticEntry[] SelectedDiagnostics()
    {
        HashSet<string> keys = DiagnosticSelectionKeys();
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
        OnDiagnosticFilterStateChanged();
    }

    private void OnClearDiagnosticFilters(object? sender, RoutedEventArgs e)
    {
        _refreshingDiagnosticFilters = true;
        try
        {
            _diagnosticSourceId = null;
            this.FindControl<TextBox>("DiagnosticTextFilter")!.Text = string.Empty;
            this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.IsChecked = false;
            this.FindControl<CheckBox>("DiagnosticIncludeWaiting")!.IsChecked = false;
            this.FindControl<ComboBox>("DiagnosticSourceFilter")!.SelectedIndex = 0;
        }
        finally { _refreshingDiagnosticFilters = false; }
        ApplyDiagnosticFilters(); RefreshSelection();
        OnDiagnosticFilterStateChanged();
    }

    private void ApplyDiagnosticFilters()
    {
        string? query = this.FindControl<TextBox>("DiagnosticTextFilter")!.Text;
        bool problems = this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.IsChecked == true;
        bool includeWaiting = this.FindControl<CheckBox>("DiagnosticIncludeWaiting")!.IsChecked == true;
        DiagnosticEntry[] matching = _entries.Where(entry => DiagnosticRetryScope.Matches(entry, _diagnosticSourceId, query, problems, includeWaiting)).ToArray();
        DiagnosticEntry[] visible = matching.Where(entry => entry.Kind != DiagnosticKind.Information).ToArray();
        if (!_visibleDiagnostics.SequenceEqual(visible) || _diagnosticGroupLanguage != Texts.Language)
        {
            DiagnosticEntry? current = CurrentDiagnosticLead;
            HashSet<string> selected = DiagnosticSelectionKeys();
            DiagnosticEntry[] retained = visible.Where(entry => selected.Contains(entry.Key)).ToArray();
            if (selected.Count > retained.Length) _diagnosticSelectionRemoved = true;
            _diagnosticSelection = retained;
            _diagnosticLead = retained.FirstOrDefault(entry => entry.Key == current?.Key) ?? retained.FirstOrDefault();
            _visibleDiagnostics = visible;
            RefreshDiagnosticGroups();
        }
        ApplyPlatformEntries(matching.Where(entry => entry.Kind == DiagnosticKind.Information).ToArray());
        RefreshEmptyState();
    }

    private void ApplyPlatformEntries(DiagnosticEntry[] platform)
    {
        DataGrid grid = this.FindControl<DataGrid>("PlatformGrid")!;
        if (!_visiblePlatform.SequenceEqual(platform))
        {
            string? key = (grid.SelectedItem as DiagnosticEntry)?.Key;
            _syncingSelection = true;
            try
            {
                _visiblePlatform = platform;
                grid.ItemsSource = platform;
                grid.SelectedItem = platform.FirstOrDefault(entry => entry.Key == key);
            }
            finally { _syncingSelection = false; }
        }
        RefreshPlatformHeader();
    }

    private void RefreshPlatformHeader()
    {
        Expander section = this.FindControl<Expander>("DiagnosticPlatformSection")!;
        section.IsVisible = _visiblePlatform.Count > 0;
        section.Header = DiagnosticGroupHeading(Texts.T("diagnostics.platform"), _visiblePlatform.Count);
    }

    // The table is empty when nothing is current (the healthy state) or when the filters hide everything; the text says which.
    private void RefreshEmptyState()
    {
        TextBlock empty = this.FindControl<TextBlock>("DiagnosticsEmpty")!;
        empty.IsVisible = _visibleDiagnostics.Count == 0;
        empty.Text = Texts.T(_entries.Any(entry => entry.Kind != DiagnosticKind.Information) ? "diagnostics.noMatch" : "diagnostics.empty");
    }

    /// <summary>Primary+F on the Diagnostics page (AP2): the message filter is the page's search field.</summary>
    internal void FocusSearch()
    {
        TextBox text = this.FindControl<TextBox>("DiagnosticTextFilter")!;
        if (!text.IsEffectivelyVisible) return;
        text.Focus(Avalonia.Input.NavigationMethod.Tab);
        text.SelectAll();
    }

    private void RefreshDiagnosticFilterLabels()
    {
        ComboBox sources = this.FindControl<ComboBox>("DiagnosticSourceFilter")!;
        TextBox text = this.FindControl<TextBox>("DiagnosticTextFilter")!;
        AutomationProperties.SetName(sources, Texts.T("sources.title"));
        AutomationProperties.SetName(text, Texts.T("diagnostics.filterText"));
        text.PlaceholderText = Texts.T("diagnostics.filterText");
        ToolTip.SetTip(text, MeowChan.Desktop.Commands.CommandBindings.Tooltip("input.focus-diagnostics-search"));
        this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.Content = Texts.T("diagnostics.onlyProblems");
        // Preserve the TextBlock content; Refresh localizes it using the latest language.
        AutomationProperties.SetName(this.FindControl<CheckBox>("DiagnosticIncludeWaiting")!, Texts.T("diagnostics.includeWaiting"));
        SetIconLabel(this.FindControl<Button>("DiagnosticClearFilters")!, "x", Texts.T("diagnostics.clearFilters"));
        RefreshPlatformHeader();
        RefreshEmptyState();
        RefreshWorkViewTexts();
    }

    // Fresh captures already describe current problems. Only revalidate identity against the
    // live entries for a frozen scope, where a refresh may have changed what the action can do.
    private int RetryableTargetCount(DiagnosticRetryTarget[] scope) => scope.Count(item =>
        item.Check && IsRetryAdmitted(item.Target, media: false) || item.Media && IsRetryAdmitted(item.Target, media: true));

    private bool IsRetryAdmitted(DiagnosticTarget target, bool media)
    {
        if (_ended || !_available || IsRemoved(target)) return false;
        return media ? RetryMediaAsync != null && CanRetryMedia?.Invoke(target) == true
            : _retry != null && _canRetry?.Invoke(target) == true;
    }

    private bool CanRetryScope(DiagnosticRetryTarget[] scope) => !_retryBusy && scope.Any(item =>
        item.Check && CanSubmitFrozen(item, media: false) || item.Media && CanSubmitFrozen(item, media: true));

    private bool CanSubmitFrozen(DiagnosticRetryTarget item, bool media)
    {
        if (!StillHasProblem(item, media) || !IsRetryAdmitted(item.Target, media)) return false;
        // An advisory callback can itself refresh/end the owner synchronously.
        return !_ended && _available && !IsRemoved(item.Target) && StillHasProblem(item, media);
    }

    private void RefreshDiagnosticBatchControls()
    {
        DiagnosticEntry[] selected = SelectedDiagnostics();
        DiagnosticRetryTarget[] selection = DiagnosticRetryScope.Capture(selected);
        DiagnosticRetryTarget[] filtered = DiagnosticRetryScope.Capture(_visibleDiagnostics);
        int filteredCount = RetryableTargetCount(filtered);
        foreach (var (name, command, count) in new[] {
            ("RetryDiagnosticSelectionButton", "diagnostics.retry-selection", RetryableTargetCount(selection)),
            ("RetryDiagnosticFilteredButton", "diagnostics.retry-filtered", filteredCount) })
        {
            Button button = this.FindControl<Button>(name)!;
            CommandBindings.ApplyLabeled(button, command, CommandCatalog.Label(command) + " (" + count + ")");
            button.IsEnabled = count > 0 && !_ended && _available && !_retryBusy;
            string scopeHelp = Texts.T("diagnostics.retryScope") + " " + Texts.T("diagnostics.retryScopeCount", count);
            ToolTip.SetTip(button, scopeHelp);
            AutomationProperties.SetHelpText(button, scopeHelp);
        }
        RefreshDiagnosticOverview(Selected != null);
        // One selected target does what "Check now" does: the selection button only exists for a set of several targets.
        this.FindControl<Button>("RetryDiagnosticSelectionButton")!.IsVisible = selection.Length > 1;
        TextBlock summary = this.FindControl<TextBlock>("DiagnosticFilterSummary")!;
        summary.IsVisible = ActiveDiagnosticFilterCount > 0 || _visibleDiagnostics.Count > 0;
        summary.Text = summary.IsVisible
            ? Texts.T("diagnostics.filterCount", _visibleDiagnostics.Count, _entries.Count(entry => entry.Kind != DiagnosticKind.Information), selected.Length)
                + " · " + Texts.Count("diagnostics.retryableTargets", filteredCount)
            : string.Empty;
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
