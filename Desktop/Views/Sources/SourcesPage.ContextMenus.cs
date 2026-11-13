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
using Serilog;

namespace MeowChan;

public partial class SourcesPage
{
    private void InitializeContextMenus()
    {
        ListBox grid = this.FindControl<ListBox>("SourceWatchesList")!;
        grid.ContextMenu = new ContextMenu();
        grid.ContextMenu.Opening += (_, args) =>
        {
            BuildWatchContextMenu(grid);
            args.Cancel = grid.ContextMenu.ItemCount == 0;
        };
        grid.AddHandler(PointerPressedEvent, OnWatchContextPointer, RoutingStrategies.Tunnel);
        grid.AddHandler(ContextRequestedEvent, OnWatchContextRequested, RoutingStrategies.Tunnel);
    }

    private void AttachSourceContextMenu(Button button, string sourceId)
    {
        button.ContextMenu = new ContextMenu();
        button.ContextMenu.Opening += (_, args) =>
        {
            ContextMenu menu = button.ContextMenu!;
            menu.Items.Clear();
            SourceRow? source = _rows.FirstOrDefault(row => row.Id == sourceId);
            if (source == null || _ended) { args.Cancel = true; return; }
            bool Ready() => !_ended && _available && !_busy;
            string reason = UnavailableReason();
            void Add(string id, Action action, bool available = true)
            {
                var item = CommandBindings.CreateMenuItem(id, action, () => Ready() && available,
                    () => Ready() && available ? null : reason);
                menu.Items.Add(item);
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
            Log.Warning(exception, "Could not open the frozen source address.");
            if (!_ended) this.FindControl<TextBlock>("SourcesActionError")!.Text = Texts.T("sources.openFailed");
        }
    }

    private void OnWatchContextPointer(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        ListBoxItem? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        grid.SelectedItem = row?.DataContext is SourceWatchRow watch ? watch : null;
        if (row == null) grid.ContextMenu?.Close();
    }

    private void OnWatchContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is ListBox grid && grid.SelectedItem is not SourceWatchRow)
            e.Handled = true;
    }

    private void BuildWatchContextMenu(ListBox grid)
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
    }

    private static string UnavailableReason() => Texts.T("menu.unavailable.target");
}
