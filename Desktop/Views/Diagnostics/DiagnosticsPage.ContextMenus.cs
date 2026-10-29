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
            BuildDiagnosticContextMenu(grid);
            args.Cancel = grid.ContextMenu.ItemCount == 0;
        };
        grid.AddHandler(PointerPressedEvent, OnDiagnosticContextPointer, RoutingStrategies.Tunnel);
        grid.AddHandler(ContextRequestedEvent, OnDiagnosticContextRequested, RoutingStrategies.Tunnel);
    }

    private bool _diagnosticPointerContextHandled;
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
            if (grid.ContextMenu?.ItemCount > 0) grid.ContextMenu.Open(grid);
        }
        else { grid.SelectedItems.Clear(); grid.ContextMenu?.Close(); }
        e.Handled = true;
    }

    private void OnDiagnosticContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not DiagnosticEntry) { e.Handled = true; return; }
        if (e.TryGetPosition(grid, out _) && _diagnosticPointerContextHandled)
        { _diagnosticPointerContextHandled = false; e.Handled = true; }
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
        if (frozen.Target is DiagnosticTarget target)
        {
            Add("watch.check", () => _ = RetryFrozenDiagnosticAsync(frozen),
                !_retryBusy && _retry != null && _canRetry?.Invoke(target) == true);
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

    private void OpenFrozenDiagnostic(DiagnosticEntry frozen)
    {
        if (_ended || !_available || frozen.Target is not DiagnosticTarget target) return;
        int generation = BeginDiagnosticAction();
        if (_openWatch?.Invoke(target) != true)
            ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Diagnostic, frozen.Key), Texts.T("diagnostics.removed"));
    }

    private async Task RetryFrozenDiagnosticAsync(DiagnosticEntry frozen)
    {
        if (_ended || !_available || _retryBusy || frozen.Target is not DiagnosticTarget target
            || _retry == null || _canRetry?.Invoke(target) != true) return;
        int generation = BeginDiagnosticAction();
        _retryBusy = true;
        RefreshSelection();
        string message;
        bool failed;
        try
        {
            CheckRequestStatus result = await _retry(target);
            message = CheckActionText.ForResult(result, target.Kind);
            failed = result is not (CheckRequestStatus.Scheduled or CheckRequestStatus.AlreadyScheduled);
        }
        catch (Exception exception) { message = Texts.T("check.failed", exception.Message); failed = true; }
        finally { _retryBusy = false; }
        if (_ended) return;
        RefreshSelection();
        ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Diagnostic, frozen.Key), message, failed);
    }
}
