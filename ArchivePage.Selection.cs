using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly HashSet<ResourceId> _archiveSelectionIds = new();
    private readonly HashSet<Control> _archivePointerContextHandled = new();
    private ResourceId? _archiveLeadId;
    internal Func<IReadOnlyList<ResourceId>, Task>? RemoveSelectedWatches { get; set; }
    internal Func<ResourceId, bool>? CanRemoveWatch { get; set; }
    internal Func<IReadOnlyList<ResourceId>, bool, Task>? CheckSelectedWatches { get; set; }
    internal Func<System.Collections.Generic.IEnumerable<string>, Task>? CopySelectedUrls { get; set; }
    internal Action<ResourceId>? SelectionDetailsRequested { get; set; }
    internal Action<ResourceId, string>? SelectionLeadAction { get; set; }

    private ArchiveThreadRow[] ArchiveVisibleRows => Control<DataGrid>("ArchiveThreadTable").ItemsSource?.Cast<ArchiveThreadRow>().ToArray() ?? Array.Empty<ArchiveThreadRow>();
    private Control ArchiveSelectionList => _threadTable ? Control<DataGrid>("ArchiveThreadTable") : Control<ListBox>("ArchiveThreads");
    private ArchiveThreadRow? ArchiveLead => _archiveSelectionIds.Count == 0 ? null
        : ArchiveVisibleRows.FirstOrDefault(row => row.Summary.Id == _archiveLeadId)
            ?? ArchiveVisibleRows.FirstOrDefault(row => _archiveSelectionIds.Contains(row.Summary.Id));
    internal string? ArchiveDirectoryFor(ResourceId id) => ArchiveVisibleRows.FirstOrDefault(row => row.Summary.Id == id)?.Summary.MediaDirectory;
    internal string ArchiveUrlFor(ResourceId id) => ArchiveVisibleRows.FirstOrDefault(row => row.Summary.Id == id)?.Summary.Url ?? string.Empty;
    private ResourceId[] FrozenArchiveSelection() => ArchiveVisibleRows.Where(row => _archiveSelectionIds.Contains(row.Summary.Id)).Select(row => row.Summary.Id).ToArray();

    private void InitializeArchiveSelection()
    {
        foreach (Control list in new Control[] { Control<ListBox>("ArchiveThreads"), Control<DataGrid>("ArchiveThreadTable") })
        {
            list.AddHandler(KeyDownEvent, OnArchiveListKeyDown, RoutingStrategies.Tunnel);
            list.AddHandler(PointerPressedEvent, OnArchiveListPointerPressed, RoutingStrategies.Tunnel);
            list.AddHandler(ContextRequestedEvent, OnArchiveContextRequested, RoutingStrategies.Tunnel);
        }
    }

    // The identity set belongs to the current 50-row page. A refreshed/filter-
    // changed page prunes unavailable IDs rather than accumulating hidden targets.
    private void RestoreArchiveSelection(ArchiveThreadRow[] rows, ResourceId? preferred = null)
    {
        HashSet<ResourceId> visible = rows.Select(row => row.Summary.Id).ToHashSet();
        _archiveSelectionIds.IntersectWith(visible);
        if (_archiveSelectionIds.Count == 0 && preferred != null && visible.Contains(preferred)) _archiveSelectionIds.Add(preferred);
        if (_archiveLeadId == null || !visible.Contains(_archiveLeadId))
            _archiveLeadId = rows.FirstOrDefault(row => _archiveSelectionIds.Contains(row.Summary.Id))?.Summary.Id;
        SynchronizeArchiveSelection(rows);
    }

    private void SynchronizeArchiveSelection(ArchiveThreadRow[] rows, object? source = null)
    {
        bool updating = _updatingList;
        _updatingList = true;
        try
        {
            ListBox cards = Control<ListBox>("ArchiveThreads");
            DataGrid table = Control<DataGrid>("ArchiveThreadTable");
            if (!ReferenceEquals(source, cards)) cards.SelectedItems!.Clear();
            if (!ReferenceEquals(source, table)) table.SelectedItems.Clear();
            foreach (ArchiveThreadRow row in rows.Where(row => _archiveSelectionIds.Contains(row.Summary.Id)))
            {
                if (!ReferenceEquals(source, cards)) cards.SelectedItems!.Add(row);
                if (!ReferenceEquals(source, table)) table.SelectedItems.Add(row);
            }
        }
        finally { _updatingList = updating; }
        _contextThread = ArchiveLead;
        UpdateArchiveSelectionActions();
    }

    private void ArchiveSelectionChanged(object? sender)
    {
        if (_updatingList || _ended || !_available) return;
        IEnumerable<ArchiveThreadRow> selected = sender is DataGrid table ? table.SelectedItems.Cast<ArchiveThreadRow>()
            : Control<ListBox>("ArchiveThreads").SelectedItems!.Cast<ArchiveThreadRow>();
        _archiveSelectionIds.Clear();
        foreach (ArchiveThreadRow row in selected) _archiveSelectionIds.Add(row.Summary.Id);
        ArchiveThreadRow? current = sender is DataGrid grid ? grid.CollectionView?.CurrentItem as ArchiveThreadRow : Control<ListBox>("ArchiveThreads").SelectedItem as ArchiveThreadRow;
        if (_archiveLeadId == null || !ArchiveVisibleRows.Any(row => row.Summary.Id == _archiveLeadId))
            _archiveLeadId = current?.Summary.Id;
        _history.Clear();
        _selectedThread = ArchiveLead?.Summary.Id;
        _showReader = false;
        SynchronizeArchiveSelection(ArchiveVisibleRows, sender);
        UpdateLayoutMode(); UpdateActions();
    }

    private void UpdateArchiveSelectionActions()
    {
        if (this.FindControl<MenuItem>("ArchiveTableRemoveMenu") is MenuItem remove)
            remove.IsEnabled = _available && !_busy && FrozenArchiveSelection().Any(id => CanRemoveWatch?.Invoke(id) == true);
    }

    private void OnArchiveListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control list || !MainWindow.IsListCommandSource(e.Source, list)) return;
        _archivePointerContextHandled.Remove(list);
        Control? hit = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(control => control is DataGridRow or ListBoxItem);
        ArchiveThreadRow? row = hit?.DataContext as ArchiveThreadRow;
        if (row != null) _archiveLeadId = row.Summary.Id;
        if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed) return;
        _archivePointerContextHandled.Add(list);
        if (row == null)
        {
            _archiveSelectionIds.Clear(); _archiveLeadId = null; _selectedThread = null;
            SynchronizeArchiveSelection(ArchiveVisibleRows);
            list.ContextMenu?.Close();
        }
        else
        {
            if (!_archiveSelectionIds.Contains(row.Summary.Id))
            { _archiveSelectionIds.Clear(); _archiveSelectionIds.Add(row.Summary.Id); }
            _selectedThread = row.Summary.Id;
            SynchronizeArchiveSelection(ArchiveVisibleRows);
            list.Focus();
            Control? owner = list.ContextMenu != null ? list : hit?.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.ContextMenu != null);
            owner?.ContextMenu?.Open(owner);
        }
        e.Handled = true;
    }

    private void OnArchiveContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control list && e.TryGetPosition(list, out _)
            && _archivePointerContextHandled.Remove(list))
            e.Handled = true;
    }

    private async void OnArchiveListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control list || e.Handled || !MainWindow.IsListCommandSource(e.Source, list)
            || _ended || !_available || _busy || _largeReader || _exportCard?.IsVisible == true) return;
        KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ArchiveThreadRow? row = list is DataGrid table ? table.CollectionView?.CurrentItem as ArchiveThreadRow
                    : (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as ArchiveThreadRow;
                if (row != null) { _archiveLeadId = row.Summary.Id; _selectedThread = row.Summary.Id; _contextThread = row; }
            }, DispatcherPriority.Background);
            return;
        }
        if (e.Key == Key.A && e.KeyModifiers == command)
        {
            e.Handled = true;
            _archiveSelectionIds.Clear(); foreach (ArchiveThreadRow row in ArchiveVisibleRows) _archiveSelectionIds.Add(row.Summary.Id);
            _archiveLeadId ??= ArchiveVisibleRows.FirstOrDefault()?.Summary.Id;
            SynchronizeArchiveSelection(ArchiveVisibleRows); return;
        }
        if (e.Key == Key.C && e.KeyModifiers == command)
        { e.Handled = true; await CopyArchiveSelectionAsync(); return; }
        if (e.Key == Key.E && e.KeyModifiers == command)
        { e.Handled = true; ResourceId[] ids = FrozenArchiveSelection(); if (ids.Length > 0) await OpenExportForThreadsAsync(ids, list); return; }
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; await RemoveArchiveSelectionAsync(); return; }
        if (e.Key == Key.F5 && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        { e.Handled = true; if (CheckSelectedWatches != null) await CheckSelectedWatches(FrozenArchiveSelection(), e.KeyModifiers == KeyModifiers.Shift); return; }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt)
        { e.Handled = true; if (ArchiveLead is ArchiveThreadRow row) SelectionDetailsRequested?.Invoke(row.Summary.Id); return; }
        if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            e.Handled = true;
            if (ArchiveLead is not ArchiveThreadRow row) return;
            ResourceId id = row.Summary.Id;
            if (e.KeyModifiers == KeyModifiers.Shift) { if (OpenGallery != null) await OpenGallery(id, null, list); }
            else { OpenReaderRequested?.Invoke(list); await LoadThreadAsync(id); }
            return;
        }
        string? action = e.Key == Key.R && e.KeyModifiers == command ? "rules"
            : e.Key == Key.O && e.KeyModifiers == command ? "folder"
            : e.Key == Key.O && e.KeyModifiers == (command | KeyModifiers.Shift) ? "browser" : null;
        if (action != null && ArchiveLead is ArchiveThreadRow lead)
        { e.Handled = true; SelectionLeadAction?.Invoke(lead.Summary.Id, action); }
    }

    private Task CopyArchiveSelectionAsync()
    {
        string[] urls = ArchiveVisibleRows.Where(row => _archiveSelectionIds.Contains(row.Summary.Id)).Select(row => row.Summary.Url).ToArray();
        return CopySelectedUrls?.Invoke(urls) ?? Task.CompletedTask;
    }
    private Task RemoveArchiveSelectionAsync()
    {
        ResourceId[] ids = FrozenArchiveSelection().Where(id => CanRemoveWatch?.Invoke(id) == true).ToArray();
        return RemoveSelectedWatches?.Invoke(ids) ?? Task.CompletedTask;
    }
    private void OnArchiveSelectionMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        MainWindow.NormalizeSelectionMenu(menu);
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            if (item.Tag as string == "selection.remove")
                item.IsEnabled = _available && !_busy && FrozenArchiveSelection().Any(id => CanRemoveWatch?.Invoke(id) == true);
    }
    private async void OnArchiveRemoveSelection(object? sender, RoutedEventArgs e) => await RemoveArchiveSelectionAsync();
    private async void OnArchiveCopySelection(object? sender, RoutedEventArgs e) => await CopyArchiveSelectionAsync();
    private async void OnArchiveCheckSelection(object? sender, RoutedEventArgs e)
    { if (CheckSelectedWatches != null) await CheckSelectedWatches(FrozenArchiveSelection(), false); }
    private async void OnArchiveRetrySelection(object? sender, RoutedEventArgs e)
    { if (CheckSelectedWatches != null) await CheckSelectedWatches(FrozenArchiveSelection(), true); }
    private void OnArchiveSelectionDetails(object? sender, RoutedEventArgs e)
    { if (ArchiveLead is ArchiveThreadRow row) SelectionDetailsRequested?.Invoke(row.Summary.Id); }
    private void OnArchiveLeadAction(object? sender, RoutedEventArgs e)
    { if (ArchiveLead is ArchiveThreadRow row && sender is MenuItem { CommandParameter: string action }) SelectionLeadAction?.Invoke(row.Summary.Id, action); }
}
