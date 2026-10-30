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
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class DiagnosticsPage
{
    private void InitializeContextMenu()
    {
        DataGrid grid = this.FindControl<DataGrid>("DiagnosticsGrid")!;
        grid.ContextMenu = new ContextMenu();
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
        else { grid.SelectedItems.Clear(); grid.ContextMenu?.Close(); _diagnosticContextPrepared = false; }
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
        menu.Items.Clear();
        DiagnosticEntry? frozen = lead ?? grid.SelectedItem as DiagnosticEntry;
        if (frozen == null || _ended) { menu.Close(); return; }
        string unavailable = Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
            ? "Für dieses Ziel derzeit nicht verfügbar." : "Currently unavailable for this target.";
        void Add(string id, Action execute, bool available)
        {
            var item = CommandBindings.CreateMenuItem(id, execute, () => !_ended && _available && available,
                () => !_ended && _available && available ? null : unavailable);
            menu.Items.Add(item);
            if (!_available || !available)
            {
                string explanation = CommandCatalog.Label(id) + ": " + unavailable;
                var info = new MenuItem { Header = explanation };
                AutomationProperties.SetName(info, explanation);
                AutomationProperties.SetHelpText(info, explanation);
                menu.Items.Add(info);
            }
        }
        DiagnosticRetryTarget[] selection = DiagnosticRetryScope.Capture(SelectedDiagnostics());
        DiagnosticRetryTarget[] filtered = DiagnosticRetryScope.Capture(_visibleDiagnostics);
        void AddBatch(string id, DiagnosticRetryTarget[] scope)
        {
            MenuItem item = CommandBindings.CreateMenuItem(id, () => _ = RetryFrozenScopeAsync(scope),
                () => CanRetryScope(scope), () => Texts.T("diagnostics.retryScope"),
                () => CommandCatalog.Label(id) + " (" + scope.Length + ")");
            item.Tag = id;
            menu.Items.Add(item);
        }
        AddBatch("diagnostics.retry-selection", selection);
        AddBatch("diagnostics.retry-filtered", filtered);
        Add("diagnostics.copy", () => _ = CopyFrozenDiagnosticAsync(frozen), _copy != null);
        if (frozen.Target is DiagnosticTarget target && frozen.Kind != DiagnosticKind.Storage)
        {
            Add("watch.check", () => _ = RetryFrozenDiagnosticAsync(frozen),
                !_retryBusy && IsCurrentDiagnostic(frozen) && _retry != null && _canRetry?.Invoke(target) == true);
            Add("watch.show-in-downloads", () => OpenFrozenDiagnostic(frozen), _openWatch != null);
        }
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
            ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Diagnostic, frozen.Key), Texts.T("diagnostics.removed"));
    }

    private async Task RetryFrozenDiagnosticAsync(DiagnosticEntry frozen)
    {
        if (_ended || !_available || _retryBusy || frozen.Kind == DiagnosticKind.Storage
            || frozen.Target is not DiagnosticTarget target || _retry == null || !IsCurrentDiagnostic(frozen)) return;
        _retryBusy = true;
        int generation = BeginDiagnosticAction();
        string? message = null;
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
        }
        catch (Exception exception) { message = Texts.T("check.failed", exception.Message); failed = true; }
        finally { _retryBusy = false; if (!_ended) RefreshSelection(); }
        if (!_ended && message != null)
            ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Diagnostic, frozen.Key), message, failed);
    }
}
