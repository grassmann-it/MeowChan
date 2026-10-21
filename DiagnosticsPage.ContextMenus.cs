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
        grid.ContextMenu.Opened += (_, _) => BuildDiagnosticContextMenu(grid);
        grid.AddHandler(PointerPressedEvent, OnDiagnosticContextPointer, RoutingStrategies.Tunnel);
        grid.AddHandler(ContextRequestedEvent, OnDiagnosticContextRequested, RoutingStrategies.Tunnel);
    }

    private void OnDiagnosticContextPointer(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        DataGridRow? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        grid.SelectedItem = row?.DataContext is DiagnosticEntry entry ? entry : null;
        if (row == null) grid.ContextMenu?.Close();
    }

    private void OnDiagnosticContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is not DiagnosticEntry)
            e.Handled = true;
    }

    private void BuildDiagnosticContextMenu(DataGrid grid)
    {
        ContextMenu menu = grid.ContextMenu!;
        menu.Items.Clear();
        if (grid.SelectedItem is not DiagnosticEntry frozen || _ended) { menu.Close(); return; }
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
        Add("diagnostics.copy", () => _ = CopyFrozenDiagnosticAsync(frozen.Details), _copy != null);
        if (frozen.Target is DiagnosticTarget target)
        {
            Add("watch.check", () => _ = RetryFrozenDiagnosticAsync(target),
                !_retryBusy && _retry != null && _canRetry?.Invoke(target) == true);
            Add("watch.show-in-downloads", () =>
            {
                if (_ended || _openWatch?.Invoke(target) != false) return;
                this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = Texts.T("diagnostics.removed");
            }, _openWatch != null);
        }
    }

    private async Task CopyFrozenDiagnosticAsync(string details)
    {
        if (_ended || _copy == null) return;
        try { await _copy(details); }
        catch (Exception)
        {
            if (!_ended) this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = Texts.T("clipboard.writeFailed");
        }
    }

    private async Task RetryFrozenDiagnosticAsync(DiagnosticTarget target)
    {
        if (_ended || !_available || _retryBusy || _retry == null || _canRetry?.Invoke(target) != true) return;
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
        this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = message;
        this.FindControl<TextBlock>("DiagnosticsActionError")!.Classes.Set("error", failed);
    }
}
