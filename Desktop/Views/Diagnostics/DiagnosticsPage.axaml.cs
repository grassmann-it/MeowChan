using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class DiagnosticsPage : UserControl
{
    private readonly Func<DiagnosticTarget, bool>? _openWatch;
    private readonly Func<string, Task>? _copy;
    private IReadOnlyList<DiagnosticEntry> _entries = Array.Empty<DiagnosticEntry>();
    private bool _available;
    private bool _refreshingDiagnostics;
    private bool _diagnosticSelectionRemoved;
    private bool _retryBusy;
    private readonly Func<DiagnosticTarget, Task<CheckRequestStatus>>? _retry;
    private readonly Func<DiagnosticTarget, bool>? _canRetry;
    private bool _ended;
    private int _actionGeneration;
    private enum ActionContextKind { Global, Diagnostic, SessionFeedback }
    private readonly record struct ActionContext(ActionContextKind Kind, string? DiagnosticKey = null, long? FeedbackId = null);
    // MessageKey, when set, re-reads the text on every render, so a language switch translates it too.
    private sealed record ActionFeedback(ActionContext Context, string Message, bool Failed, string? MessageKey = null);
    // Watches the host reported as removed (a Stale check result). Their rows stay until the next
    // snapshot, which drops them; until then their watch actions are off and say why.
    private readonly HashSet<DiagnosticTarget> _removedTargets = new();
    private bool IsRemoved(DiagnosticTarget? target) => target != null && _removedTargets.Contains(target);
    private ActionFeedback? _actionFeedback;
    internal Func<IReadOnlyList<MeowChan.Downloader.DownloadTargetWaitState>>? GetTargetWaitStates { get; set; }
    internal Func<Task>? CheckTargetsAsync { get; set; }

    public DiagnosticsPage()
    {
        InitializeComponent(); InitializeContextMenu(); _diagnosticFiltersReady = true;
        // A page that is shown before its first session refresh still carries its labels and headers.
        ApplyStaticLabels();
        RefreshSelection();
    }
    internal DiagnosticsPage(Func<DiagnosticTarget, bool> openWatch, Func<string, Task> copy,
        Func<DiagnosticTarget, Task<CheckRequestStatus>>? retry = null, Func<DiagnosticTarget, bool>? canRetry = null) : this()
    {
        _openWatch = openWatch;
        _copy = copy;
        _retry = retry;
        _canRetry = canRetry;
    }

    /// <summary>
    /// Shows the current session. <paramref name="snapshot"/> is the snapshot the Status page already took for its status line
    /// (same view, same wait states); without one the page takes its own, so a page used on its own stays complete.
    /// </summary>
    internal void Refresh(DesktopSessionView view, bool available, DiagnosticsSnapshot? snapshot = null)
    {
        if (_ended) return;
        _available = available;
        _removedTargets.Clear();
        snapshot ??= DiagnosticsSnapshot.From(view, GetTargetWaitStates?.Invoke());
        _entries = snapshot.Entries.Concat(PlatformDiagnostics.Entries()).ToArray(); // problems first, platform facts last
        RefreshDiagnosticSources();
        ApplyDiagnosticFilters();
        ApplyStaticLabels();
        RefreshSelection();
        RefreshFeedback();
    }

    // Labels, column headers and filter texts that need no session snapshot.
    private void ApplyStaticLabels()
    {
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button && !button.Classes.Contains("command-icon")) button.Content = Texts.T(key);
        }
        // The privacy note is help, not page text: it sits on the copy buttons (tooltip and accessible help) and nowhere else.
        Button copy = this.FindControl<Button>("CopyDiagnosticButton")!;
        CommandBindings.ApplyLabeled(copy, "diagnostics.copy", Texts.T("diagnostics.copy"));
        ToolTip.SetTip(copy, Texts.T("diagnostics.copyPrivacy"));
        AutomationProperties.SetHelpText(copy, Texts.T("diagnostics.copyPrivacy"));
        ApplyFeedbackButtonLabels();
        CommandBindings.ApplyLabeled(this.FindControl<Button>("OpenDiagnosticWatchButton")!, "watch.show-in-downloads", Texts.T("diagnostics.openWatch"));
        DataGrid grid = this.FindControl<DataGrid>("DiagnosticsGrid")!;
        grid.Columns[0].Header = Texts.T("diagnostics.category");
        grid.Columns[1].Header = Texts.T("diagnostics.resource");
        grid.Columns[2].Header = Texts.T("diagnostics.message");
        this.FindControl<DataGrid>("PlatformGrid")!.Columns[0].Header = Texts.T("diagnostics.message");
        AutomationProperties.SetName(this.FindControl<DataGrid>("PlatformGrid")!, Texts.T("diagnostics.platform"));
        RefreshDiagnosticFilterLabels();
    }

    // One selection across the two tables: the platform facts (own group) and the requests and problems never both hold a row.
    private DiagnosticEntry? Selected => this.FindControl<DataGrid>("DiagnosticsGrid")!.SelectedItem as DiagnosticEntry
        ?? this.FindControl<DataGrid>("PlatformGrid")!.SelectedItem as DiagnosticEntry;
    private bool _syncingSelection;
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingDiagnostics) _diagnosticSelectionRemoved = false;
        if (!_syncingSelection && this.FindControl<DataGrid>("DiagnosticsGrid")!.SelectedItem != null)
            ClearOtherSelection(this.FindControl<DataGrid>("PlatformGrid")!);
        RefreshSelection();
    }
    private void OnPlatformSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && this.FindControl<DataGrid>("PlatformGrid")!.SelectedItem != null)
            ClearOtherSelection(this.FindControl<DataGrid>("DiagnosticsGrid")!);
        RefreshSelection();
    }
    private void ClearOtherSelection(DataGrid other)
    {
        _syncingSelection = true;
        try { other.SelectedItems.Clear(); }
        finally { _syncingSelection = false; }
    }

    // The table row takes the free height (Height *): the content root is at least as tall as the viewport, and the table
    // may grow to it. Below the shell minimum the page scrolls instead of squeezing the table to nothing.
    private void OnDiagnosticScrollSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        double viewport = e.NewSize.Height;
        this.FindControl<Grid>("DiagnosticsContentRoot")!.MinHeight = viewport;
        this.FindControl<DataGrid>("DiagnosticsGrid")!.MaxHeight = Math.Max(viewport, 480);
    }

    private void OnDiagnosticPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Both panes stay mounted: resizing never replaces a selected target,
        // changes its command context or hides the focused details editor.
        bool wide = Bounds.Width >= 1000;
        Grid split = this.FindControl<Grid>("DiagnosticSplit")!;
        split.ColumnDefinitions[0].Width = new GridLength(wide ? 3 : 1, GridUnitType.Star);
        split.ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        split.ColumnSpacing = wide ? 12 : 0;
        split.RowSpacing = wide ? 0 : 12;
        Control detail = this.FindControl<Border>("DiagnosticDetailPane")!;
        Grid.SetColumn(detail, wide ? 1 : 0);
        Grid.SetRow(detail, wide ? 0 : 1);
    }
    private void RefreshSelection()
    {
        DiagnosticEntry? selected = Selected;
        TextBlock hint = this.FindControl<TextBlock>("DiagnosticSelectionHint")!;
        hint.Text = Texts.T(_diagnosticSelectionRemoved ? "diagnostics.selectionGone" : "diagnostics.choose");
        hint.IsVisible = selected == null;
        TextBlock title = this.FindControl<TextBlock>("DiagnosticSelectionTitle")!;
        title.Text = selected?.Summary ?? string.Empty;
        title.IsVisible = selected != null;
        TextBlock context = this.FindControl<TextBlock>("DiagnosticSelectionContext")!;
        context.Text = selected == null ? string.Empty : selected.Category + " · " + selected.Resource
            + (selected.ResourceId == null ? string.Empty : " · " + selected.ResourceId.Source + " /" + selected.ResourceId.Board + "/ " + selected.ResourceId.Thread);
        context.IsVisible = selected != null;
        this.FindControl<TextBox>("DiagnosticDetails")!.Text = selected?.Details ?? string.Empty;
        this.FindControl<TextBox>("DiagnosticDetails")!.IsVisible = selected != null;
        AutomationProperties.SetName(this.FindControl<TextBox>("DiagnosticDetails")!,
            selected == null ? Texts.T("diagnostics.choose") : selected.Summary);
        this.FindControl<Button>("CopyDiagnosticButton")!.IsEnabled = _available && Selected != null;
        // A removed watch keeps its rows until the next snapshot; its watch actions stay off and say why.
        bool removed = IsRemoved(Selected?.Target);
        string? removedReason = removed ? Texts.T("diagnostics.removed") : null;
        Button open = this.FindControl<Button>("OpenDiagnosticWatchButton")!;
        open.IsEnabled = _available && Selected?.Target != null && !removed;
        ToolTip.SetTip(open, removedReason);
        Button retry = this.FindControl<Button>("RetryDiagnosticButton")!;
        CommandBindings.ApplyLabeled(retry, "watch.check",
            Texts.T(Selected?.DownloadTargetPath != null ? "targets.check" : Selected?.Target?.Kind == DiagnosticTargetKind.Board ? "check.boardAction" : "check.action"));
        retry.IsEnabled = _available && !_retryBusy
            && (Selected?.DownloadTargetPath != null && CheckTargetsAsync != null
                || Selected?.Target is DiagnosticTarget target && !removed && _retry != null && _canRetry?.Invoke(target) == true);
        ToolTip.SetTip(retry, removedReason);
        RefreshDiagnosticBatchControls();
        RenderDiagnosticActionFeedback();
    }
    private int BeginDiagnosticAction()
    {
        _actionFeedback = null;
        RenderDiagnosticActionFeedback();
        return ++_actionGeneration;
    }
    private void ShowDiagnosticActionFeedback(int generation, ActionContext context, string message, bool failed = true,
        string? messageKey = null)
    {
        if (_ended || generation != _actionGeneration) return;
        if (context.Kind == ActionContextKind.SessionFeedback)
            AutomationProperties.SetLiveSetting(this.FindControl<TextBlock>("DiagnosticsActionError")!, AutomationLiveSetting.Assertive);
        _actionFeedback = new(context, message, failed, messageKey);
        RenderDiagnosticActionFeedback();
    }
    private void RenderDiagnosticActionFeedback()
    {
        if (_ended) return;
        bool current = _actionFeedback?.Context is ActionContext context && (context.Kind switch
        {
            ActionContextKind.Global => true,
            ActionContextKind.Diagnostic => context.DiagnosticKey == Selected?.Key,
            ActionContextKind.SessionFeedback => context.FeedbackId == (this.FindControl<DataGrid>("SessionFeedbackGrid")!.SelectedItem as Desktop.Feedback.SessionFeedback)?.Id,
            _ => false
        });
        TextBlock text = this.FindControl<TextBlock>("DiagnosticsActionError")!;
        text.Text = !current ? string.Empty
            : _actionFeedback!.MessageKey is { } key ? Texts.T(key) : _actionFeedback.Message;
    }
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || Selected is not DiagnosticEntry selected || _copy == null) return;
        await CopyFrozenDiagnosticAsync(selected);
    }
    private void OnOpenWatchClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is DiagnosticEntry selected) OpenFrozenDiagnostic(selected);
    }
    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        DiagnosticEntry? selected = Selected;
        if (!_ended && _available && !_retryBusy && selected?.DownloadTargetPath != null && CheckTargetsAsync != null)
        {
            ActionContext context = new(ActionContextKind.Diagnostic, selected.Key);
            int generation = BeginDiagnosticAction();
            _retryBusy = true; RefreshSelection();
            string? error = null;
            try { await CheckTargetsAsync(); }
            catch (Exception exception) { error = exception.Message; }
            finally { _retryBusy = false; if (!_ended) RefreshSelection(); }
            if (error != null) ShowDiagnosticActionFeedback(generation, context, error);
            return;
        }
        if (selected != null) await RetryFrozenDiagnosticAsync(selected);
    }
    internal void EndLifetime() { _ended = true; _available = false; if (_feedback != null) _feedback.Changed -= RefreshFeedback; }
}
