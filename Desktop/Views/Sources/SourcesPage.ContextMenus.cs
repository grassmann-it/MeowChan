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
using Serilog;
using Ursa.Controls;

namespace MeowChan;

public partial class SourcesPage
{
    private void InitializeContextMenus()
    {
        ListBox grid = this.FindControl<ListBox>("SourceWatchesList")!;
        grid.ContextMenu = ContextMenuBuilder.Empty();
        grid.ContextMenu.Opening += (_, args) =>
        {
            BuildWatchContextMenu(grid);
            args.Cancel = grid.ContextMenu.ItemCount == 0;
        };
        grid.AddHandler(PointerPressedEvent, OnWatchContextPointer, RoutingStrategies.Tunnel);
        grid.AddHandler(ContextRequestedEvent, OnWatchContextRequested, RoutingStrategies.Tunnel);
    }

    private void AttachSourceContextMenu(SelectionListItem button, string sourceId)
    {
        button.ContextMenu = ContextMenuBuilder.Empty();
        button.ContextMenu.Opening += (_, args) => args.Cancel = !BuildSourceContextMenu(button, sourceId);
    }

    /// <summary>Opens the card's menu from the keyboard. Avalonia's explicit <c>Open</c> skips <c>Opening</c>, so the menu is
    /// composed first; with nothing to show it stays closed.</summary>
    internal bool OpenSourceContextMenu(SelectionListItem button, string sourceId)
    {
        if (button.ContextMenu is not { } menu || !BuildSourceContextMenu(button, sourceId)) return false;
        menu.Open(button);
        return true;
    }

    private bool BuildSourceContextMenu(SelectionListItem button, string sourceId)
    {
        ContextMenu menu = button.ContextMenu!;
        SourceListRow? source = _rows.FirstOrDefault(row => row.Id == sourceId);
        if (source == null || _ended) return false;
        bool Ready() => !_ended && !IsBusy;
        string reason = UnavailableReason();
        ContextMenuBuilder builder = new ContextMenuBuilder().Named(source.Name);
        // Selecting and opening the address need no session; rules and diagnostics do (same gates as the detail buttons).
        void Add(string id, Action action, bool available = true, bool needsSession = true)
        {
            bool CanExecute() => Ready() && available && (!needsSession || _available);
            builder.Command(id, action, CanExecute(), CanExecute() ? null : reason,
                availableWhen: CanExecute, reasonWhen: () => CanExecute() ? null : reason);
        }
        Add("sources.select", () =>
        {
            if (_ended || !_rows.Any(row => row.Id == source.Id)) return;
            if (_selectedId != source.Id) SetActionError(string.Empty);
            SetSelectedSource(source.Id);
            _showDetails = true; RenderDetails();
            FocusSourceControlAfterLayout(this.FindControl<TextBlock>("SourceHeading")!);
        }, needsSession: false);
        Add("location.open-source", () => _ = OpenFrozenSourceAsync(source.Address), needsSession: false);
        Add("rules.edit-scope", () => _editRules?.Invoke(source.Id), _editRules != null);
        Add("navigation.diagnostics", () => _openDiagnostics?.Invoke(source.Id), _openDiagnostics != null);
        if (source.Plugin is { } plugin)
        {
            if (!IsBusy) { SetSelectedSource(sourceId); RenderDetails(); }
            SourcePluginManagement.AppendSourcePluginActions(builder, plugin.Key, button);
        }
        builder.Populate(menu);
        return true;
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
        if (grid.SelectedItem is not SourceWatchRow frozen || _ended) { ContextMenuBuilder.Clear(menu); menu.Close(); return; }
        bool Ready() => !_ended && _available && !IsBusy && _openWatch != null;
        string reason = UnavailableReason();
        ContextMenuBuilder builder = new ContextMenuBuilder().Named(Texts.T("sources.watches"));
        bool CanExecute() => Ready();
        builder.Command("watch.show-in-downloads", () =>
        {
            if (_ended || _openWatch?.Invoke(frozen.Target) != false) return;
            this.FindControl<TextBlock>("SourcesActionError")!.Text = Texts.T("diagnostics.removed");
        }, CanExecute(), CanExecute() ? null : reason, availableWhen: CanExecute,
            reasonWhen: () => CanExecute() ? null : reason);
        builder.Populate(menu);
    }

    private static string UnavailableReason() => Texts.T("menu.unavailable.target");
}
