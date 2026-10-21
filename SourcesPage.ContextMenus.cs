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

namespace MeowChan;

public partial class SourcesPage
{
    private void InitializeContextMenus()
    {
        DataGrid grid = this.FindControl<DataGrid>("SourceWatchesGrid")!;
        grid.ContextMenu = new ContextMenu();
        grid.ContextMenu.Opened += (_, _) => BuildWatchContextMenu(grid);
        grid.AddHandler(PointerPressedEvent, OnWatchContextPointer, RoutingStrategies.Tunnel);
        grid.AddHandler(ContextRequestedEvent, OnWatchContextRequested, RoutingStrategies.Tunnel);
    }

    private void AttachSourceContextMenu(Button button, string sourceId)
    {
        button.ContextMenu = new ContextMenu();
        button.ContextMenu.Opened += (_, _) =>
        {
            ContextMenu menu = button.ContextMenu!;
            menu.Items.Clear();
            SourceRow? source = _rows.FirstOrDefault(row => row.Id == sourceId);
            if (source == null || _ended) { menu.Close(); return; }
            bool Ready() => !_ended && _available && !_busy;
            string reason = UnavailableReason();
            void Add(string id, Action action, bool available = true)
            {
                var item = CommandBindings.CreateMenuItem(id, action, () => Ready() && available,
                    () => Ready() && available ? null : reason);
                menu.Items.Add(item);
                if (!Ready() || !available) AddReason(menu, CommandCatalog.Label(id), reason);
            }
            Add("sources.select", () =>
            {
                if (_ended || !_rows.Any(row => row.Id == source.Id)) return;
                _selectedId = source.Id; _showDetails = true; RenderDetails();
            });
            Add("location.open-source", () => _ = OpenFrozenSourceAsync(source.Address));
            Add("rules.edit-scope", () => _editRules?.Invoke(source.Id), _editRules != null);
            Add("navigation.diagnostics", () => _openDiagnostics?.Invoke(), _openDiagnostics != null);
        };
    }

    private async Task OpenFrozenSourceAsync(string address)
    {
        try { await ExternalLauncher.OpenAsync(address); }
        catch (Exception exception)
        {
            if (!_ended) this.FindControl<TextBlock>("SourcesActionError")!.Text = exception.Message;
        }
    }

    private void OnWatchContextPointer(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        DataGridRow? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        grid.SelectedItem = row?.DataContext is SourceWatchRow watch ? watch : null;
        if (row == null) grid.ContextMenu?.Close();
    }

    private void OnWatchContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is not SourceWatchRow)
            e.Handled = true;
    }

    private void BuildWatchContextMenu(DataGrid grid)
    {
        ContextMenu menu = grid.ContextMenu!;
        menu.Items.Clear();
        if (grid.SelectedItem is not SourceWatchRow frozen || _ended) { menu.Close(); return; }
        bool Ready() => !_ended && _available && !_busy && _openWatch != null;
        string reason = UnavailableReason();
        menu.Items.Add(CommandBindings.CreateMenuItem("watch.show-in-downloads", () =>
        {
            if (_ended || _openWatch?.Invoke(frozen.Target) != false) return;
            this.FindControl<TextBlock>("SourcesActionError")!.Text = Texts.T("diagnostics.removed");
        }, Ready, () => Ready() ? null : reason));
        if (!Ready()) AddReason(menu, CommandCatalog.Label("watch.show-in-downloads"), reason);
    }

    private static string UnavailableReason() => Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
        ? "Für dieses Ziel derzeit nicht verfügbar." : "Currently unavailable for this target.";

    private static void AddReason(ContextMenu menu, string label, string reason)
    {
        string explanation = label + ": " + reason;
        var info = new MenuItem { Header = explanation };
        AutomationProperties.SetName(info, explanation);
        AutomationProperties.SetHelpText(info, explanation);
        menu.Items.Add(info);
    }
}
