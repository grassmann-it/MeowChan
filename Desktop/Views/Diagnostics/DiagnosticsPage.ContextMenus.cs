using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class DiagnosticsPage
{
    private void InitializeContextMenu()
    {
        InitializeContextMenu(this.FindControl<DataGrid>("DiagnosticsGrid")!);
        InitializeContextMenu(this.FindControl<DataGrid>("PlatformGrid")!);
    }

    private void InitializeContextMenu(DataGrid grid)
    {
        grid.ContextMenu = ContextMenuBuilder.Empty();
        grid.ContextMenu.Opening += (_, args) =>
        {
            // Pointer invocation has already frozen both clicked lead and batch
            // arrays. Opening must not rebuild B's menu from selection lead A.
            if (!_diagnosticContextPrepared)
            {
                BuildDiagnosticContextMenu(grid);
                _diagnosticContextPrepared = true;
            }
            args.Cancel = grid.ContextMenu.ItemCount == 0;
        };
        grid.ContextMenu.Closed += (_, _) => _diagnosticContextPrepared = false;
        grid.AddHandler(PointerPressedEvent, OnDiagnosticContextPointer, RoutingStrategies.Tunnel);
        grid.AddHandler(ContextRequestedEvent, OnDiagnosticContextRequested, RoutingStrategies.Tunnel);
    }

    /// <summary>The menu of the selected row, opened by the context-open command: the selection is captured once, as for the Menu key.</summary>
    internal void OpenDiagnosticKeyboardMenu(DataGrid grid)
    {
        if (_ended || grid.SelectedItem is not DiagnosticEntry || grid.ContextMenu is not { } menu) return;
        // Avalonia's explicit Open skips the menu's Opening event, so the menu is composed here (once, marked prepared) and only
        // opened when it has entries; a stale or empty menu must never pop up.
        BuildDiagnosticContextMenu(grid);
        _diagnosticContextPrepared = true;
        if (menu.ItemCount > 0) menu.Open(grid); else _diagnosticContextPrepared = false;
    }

    private bool _diagnosticPointerContextHandled;
    private bool _diagnosticContextPrepared;
    private void OnDiagnosticContextPointer(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        DataGridRow? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        _diagnosticPointerContextHandled = true;
        if (row?.DataContext is DiagnosticEntry entry)
        {
            if (!grid.SelectedItems.Contains(entry)) grid.SelectedItem = entry;
            grid.Focus();
            BuildDiagnosticContextMenu(grid, entry);
            _diagnosticContextPrepared = true;
            if (grid.ContextMenu?.ItemCount > 0) grid.ContextMenu.Open(grid);
        }
        else { MeowChan.Desktop.Controls.DataGridSelection.Clear(grid); grid.ContextMenu?.Close(); _diagnosticContextPrepared = false; }
        e.Handled = true;
    }

    private void OnDiagnosticContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        bool pointer = e.TryGetPosition(grid, out _);
        if (pointer && _diagnosticPointerContextHandled)
        { _diagnosticPointerContextHandled = false; e.Handled = true; return; }
        _diagnosticPointerContextHandled = false;
        if (grid.SelectedItem is not DiagnosticEntry) { e.Handled = true; return; }
        // Keyboard context invocation always captures the current selection once.
        _diagnosticContextPrepared = false;
    }

    private void BuildDiagnosticContextMenu(DataGrid grid, DiagnosticEntry? lead = null)
    {
        ContextMenu menu = grid.ContextMenu!;
        DiagnosticEntry? frozen = lead ?? grid.SelectedItem as DiagnosticEntry;
        if (frozen == null || _ended) { ContextMenuBuilder.Clear(menu); menu.Close(); return; }
        string unavailable = Texts.T("menu.unavailable.target");
        ContextMenuBuilder builder = new ContextMenuBuilder().Named(Texts.T("nav.diagnostics"));
        void Add(string id, Action execute, bool available)
        {
            bool CanExecute() => !_ended && _available && available;
            builder.Command(id, execute, CanExecute(), CanExecute() ? null : unavailable,
                availableWhen: CanExecute, reasonWhen: () => CanExecute() ? null : unavailable);
        }
        DiagnosticRetryTarget[] selection = DiagnosticRetryScope.Capture(SelectedDiagnostics());
        DiagnosticRetryTarget[] filtered = DiagnosticRetryScope.Capture(_visibleDiagnostics);
        void AddBatch(string id, DiagnosticRetryTarget[] scope)
        {
            bool CanExecute() => CanRetryScope(scope);
            string? Reason() => CanExecute() ? null : Texts.T("diagnostics.retryScope");
            builder.Command(id, () => _ = RetryFrozenScopeAsync(scope), CanExecute(), Reason(),
                CommandCatalog.Label(id) + " (" + RetryableTargetCount(scope) + ")", availableWhen: CanExecute, reasonWhen: Reason);
        }
        if (frozen.Kind != DiagnosticKind.Information)
        {
            AddBatch("diagnostics.retry-selection", selection);
            AddBatch("diagnostics.retry-filtered", filtered);
        }
        Add("diagnostics.copy", () => _ = CopyFrozenDiagnosticAsync(frozen), _copy != null);
        if (frozen.Target is DiagnosticTarget target && frozen.Kind != DiagnosticKind.Storage)
        {
            Add("watch.check", () => _ = RetryFrozenDiagnosticAsync(frozen),
                !_retryBusy && !IsRemoved(target) && IsCurrentDiagnostic(frozen) && _retry != null && _canRetry?.Invoke(target) == true);
            Add("watch.show-in-downloads", () => OpenFrozenDiagnostic(frozen), _openWatch != null && !IsRemoved(target));
        }
        builder.Populate(menu);
    }

    private async Task CopyFrozenDiagnosticAsync(DiagnosticEntry frozen)
    {
        if (_ended || !_available || _copy == null) return;
        int generation = BeginDiagnosticAction();
        try { await _copy(frozen.Details); }
        catch (Exception)
        {
            if (_ended) return;
            string message = Texts.T("clipboard.writeFailed");
            ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Diagnostic, frozen.Key), message);
            _feedback?.Record(Desktop.Feedback.FeedbackCategory.Error, "diagnostics.copy", frozen.Key, message);
        }
    }

    private bool IsCurrentDiagnostic(DiagnosticEntry frozen) => _entries.Any(entry =>
        entry.Key == frozen.Key && entry.Target == frozen.Target && entry.ResourceId == frozen.ResourceId);

    private void OpenFrozenDiagnostic(DiagnosticEntry frozen)
    {
        if (_ended || !_available || frozen.Target is not DiagnosticTarget target) return;
        int generation = BeginDiagnosticAction();
        if (!IsCurrentDiagnostic(frozen) || _openWatch?.Invoke(target) != true)
            ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Diagnostic, frozen.Key), Texts.T("diagnostics.removed"),
                messageKey: "diagnostics.removed");
    }

    private async Task RetryFrozenDiagnosticAsync(DiagnosticEntry frozen)
    {
        if (_ended || !_available || _retryBusy || frozen.Kind == DiagnosticKind.Storage
            || frozen.Target is not DiagnosticTarget target || _retry == null || !IsCurrentDiagnostic(frozen)) return;
        _retryBusy = true;
        int generation = BeginDiagnosticAction();
        string? message = null, messageKey = null;
        bool failed = false;
        try
        {
            RefreshSelection();
            // Advisory eligibility is re-read under the page's busy lease. The
            // queued host operation MUST still do its own authoritative admission.
            if (_canRetry?.Invoke(target) != true || _ended || !_available || !IsCurrentDiagnostic(frozen)) return;
            CheckRequestStatus result = await _retry(target);
            message = CheckActionText.ForResult(result, target.Kind);
            failed = result is not (CheckRequestStatus.Scheduled or CheckRequestStatus.AlreadyScheduled);
            if (result == CheckRequestStatus.Stale) { _removedTargets.Add(target); messageKey = "diagnostics.removed"; }
        }
        catch (Exception exception) { message = Texts.T("check.failed", exception.Message); failed = true; }
        finally { _retryBusy = false; if (!_ended) RefreshSelection(); }
        if (!_ended && message != null)
            ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Diagnostic, frozen.Key), message, failed, messageKey);
    }
}
