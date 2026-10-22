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
    internal Func<IReadOnlyList<ResourceId>, Control?, Task>? DeletePermanentlyRequested { get; set; }
    internal Func<ResourceId, bool>? CanRemoveWatch { get; set; }
    internal Func<ResourceId, bool>? CanCheckWatch { get; set; }
    internal Func<ResourceId, bool>? CanRetryWatch { get; set; }
    private ArchiveThreadRow? _archiveCommandLead;
    internal Func<IReadOnlyList<ResourceId>, bool, Task>? CheckSelectedWatches { get; set; }
    internal Func<System.Collections.Generic.IEnumerable<string>, Task>? CopySelectedUrls { get; set; }
    internal Action<ResourceId>? SelectionDetailsRequested { get; set; }
    internal Action<ResourceId, string>? SelectionLeadAction { get; set; }

    private ArchiveThreadRow[] ArchiveVisibleRows => Control<DataGrid>("ArchiveThreadTable").ItemsSource?.Cast<ArchiveThreadRow>().ToArray() ?? Array.Empty<ArchiveThreadRow>();
    private Control ArchiveSelectionList => _threadTable ? Control<DataGrid>("ArchiveThreadTable") : Control<ListBox>("ArchiveThreads");
    private ArchiveThreadRow? ArchiveLead
    {
        get
        {
            ArchiveThreadRow[] rows = ArchiveVisibleRows;
            ResourceId? id = ResolveArchiveLeadId(rows.Select(row => row.Summary.Id), _archiveSelectionIds, _archiveLeadId);
            return id == null ? null : rows.FirstOrDefault(row => row.Summary.Id == id);
        }
    }
    internal static ResourceId? ResolveArchiveLeadId(IEnumerable<ResourceId> visible,
        IReadOnlySet<ResourceId> selected, ResourceId? preferred, ResourceId? current = null)
    {
        ResourceId[] rows = visible.ToArray();
        if (preferred != null && selected.Contains(preferred) && rows.Contains(preferred)) return preferred;
        if (current != null && selected.Contains(current) && rows.Contains(current)) return current;
        return rows.FirstOrDefault(selected.Contains);
    }
    internal string? ArchiveDirectoryFor(ResourceId id) => (_archiveCommandLead?.Summary.Id == id ? _archiveCommandLead : ArchiveVisibleRows.FirstOrDefault(row => row.Summary.Id == id))?.Summary.MediaDirectory;
    internal string ArchiveUrlFor(ResourceId id) => (_archiveCommandLead?.Summary.Id == id ? _archiveCommandLead : ArchiveVisibleRows.FirstOrDefault(row => row.Summary.Id == id))?.Summary.Url ?? string.Empty;
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
        _archiveLeadId = ResolveArchiveLeadId(rows.Select(row => row.Summary.Id), _archiveSelectionIds, _archiveLeadId);
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

    private async void ArchiveSelectionChanged(object? sender)
    {
        if (_updatingList || _ended || !_available) return;
        IEnumerable<ArchiveThreadRow> selected = sender is DataGrid table ? table.SelectedItems.Cast<ArchiveThreadRow>()
            : (sender as ListBox ?? Control<ListBox>("ArchiveThreads")).SelectedItems!.Cast<ArchiveThreadRow>();
        _archiveSelectionIds.Clear();
        foreach (ArchiveThreadRow row in selected) _archiveSelectionIds.Add(row.Summary.Id);
        ArchiveThreadRow? current = sender is DataGrid grid ? grid.CollectionView?.CurrentItem as ArchiveThreadRow : (sender as ListBox ?? Control<ListBox>("ArchiveThreads")).SelectedItem as ArchiveThreadRow;
        _archiveLeadId = ResolveArchiveLeadId(ArchiveVisibleRows.Select(row => row.Summary.Id),
            _archiveSelectionIds, _archiveLeadId, current?.Summary.Id);
        _history.Clear();
        _selectedThread = ArchiveLead?.Summary.Id;
        _showReader = false;
        SynchronizeArchiveSelection(ArchiveVisibleRows, sender);
        UpdateLayoutMode(); UpdateActions();
        if (CanShowArchivePreviewBesideList && ArchiveLead is ArchiveThreadRow lead)
            await LoadThreadAsync(lead.Summary.Id);
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
        if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed)
        {
            if (row != null) Dispatcher.UIThread.Post(async () =>
            {
                if (!_ended && _available && CanShowArchivePreviewBesideList
                    && _archiveLeadId == row.Summary.Id && _archiveSelectionIds.Contains(row.Summary.Id)
                    && _selectedThread != row.Summary.Id)
                    await LoadThreadAsync(row.Summary.Id);
            }, DispatcherPriority.Background);
            return;
        }
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
            if (owner != null) OpenArchiveSelectionMenu(owner, e.GetPosition(owner));
        }
        e.Handled = true;
    }

    private void OnArchiveContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control list) return;
        if (e.TryGetPosition(list, out _))
        {
            // Pointer menus are opened explicitly only after resolving an actual row.
            _archivePointerContextHandled.Remove(list);
            e.Handled = true;
            return;
        }
        e.Handled = true;
        if (ArchiveLead is not ArchiveThreadRow lead) return;
        Control? owner = list.ContextMenu != null ? list : list.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.ContextMenu != null && control.DataContext is ArchiveThreadRow row && row.Summary.Id == lead.Summary.Id);
        if (owner == null) return;
        Control? leadRow = list.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control is DataGridRow or ListBoxItem
                && control.DataContext is ArchiveThreadRow row && row.Summary.Id == lead.Summary.Id);
        Point anchor = leadRow?.TranslatePoint(new Point(0, leadRow.Bounds.Height), owner) ?? default;
        OpenArchiveSelectionMenu(owner, anchor);
    }

    private async void OnArchiveListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control list || e.Handled || !MainWindow.IsListCommandSource(e.Source, list)
            || _ended || !_available || _largeReader || Control<Border>("ArchiveExportDialog").IsVisible) return;
        if (MatchesArchiveCommand(e, "input.focus-search"))
        { e.Handled = true; Control<TextBox>("ArchiveSearchText").Focus(); return; }
        if (MatchesArchiveCommand(e, "archive.previous-page") || MatchesArchiveCommand(e, "archive.next-page"))
        {
            e.Handled = true;
            Button pageButton = Control<Button>(e.Key == Key.PageUp ? "PreviousThreadsButton" : "NextThreadsButton");
            if (pageButton.IsEnabled)
            {
                if (e.Key == Key.PageUp) OnPreviousThreads(sender, e);
                else OnNextThreads(sender, e);
            }
            return;
        }
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                ArchiveThreadRow? row = list is DataGrid table ? table.CollectionView?.CurrentItem as ArchiveThreadRow
                    : (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as ArchiveThreadRow;
                if (row != null && _archiveSelectionIds.Contains(row.Summary.Id))
                {
                    _archiveLeadId = row.Summary.Id; _contextThread = row;
                    if (CanShowArchivePreviewBesideList &&
                        (_selectedThread != row.Summary.Id || (!_busy && _thread?.Summary.Id != row.Summary.Id)))
                        await LoadThreadAsync(row.Summary.Id);
                }
            }, DispatcherPriority.Background);
            return;
        }
        if (_busy) return;
        if (MatchesArchiveCommand(e, "archive.select-page"))
        {
            e.Handled = true;
            _archiveSelectionIds.Clear(); foreach (ArchiveThreadRow row in ArchiveVisibleRows) _archiveSelectionIds.Add(row.Summary.Id);
            _archiveLeadId ??= ArchiveVisibleRows.FirstOrDefault()?.Summary.Id;
            SynchronizeArchiveSelection(ArchiveVisibleRows); return;
        }
        if (MatchesArchiveCommand(e, "selection.copy-links"))
        { e.Handled = true; await CopyArchiveSelectionAsync(); return; }
        if (MatchesArchiveCommand(e, "export.open-html-dialog"))
        { e.Handled = true; ResourceId[] ids = FrozenArchiveSelection(); if (ids.Length > 0) await OpenExportForThreadsAsync(ids, list); return; }
        if (MatchesArchiveCommand(e, "archive.delete-permanently"))
        {
            e.Handled = true;
            ResourceId[] ids = FrozenArchiveSelection();
            if (ids.Length > 0 && DeletePermanentlyRequested != null) await DeletePermanentlyRequested(ids, list);
            return;
        }
        if (MatchesArchiveCommand(e, "watch.remove"))
        { e.Handled = true; await RemoveArchiveSelectionAsync(); return; }
        if (MatchesArchiveCommand(e, "watch.check") || MatchesArchiveCommand(e, "watch.retry-media"))
        { e.Handled = true; if (CheckSelectedWatches != null) await CheckArchiveSelectionAsync(e.KeyModifiers == KeyModifiers.Shift); return; }
        if (MatchesArchiveCommand(e, "thread.details"))
        { e.Handled = true; if (ArchiveLead is ArchiveThreadRow row) await ShowArchiveDetailsAsync(row); return; }
        if (MatchesArchiveCommand(e, "thread.read") || MatchesArchiveCommand(e, "gallery.open"))
        {
            e.Handled = true;
            if (ArchiveLead is not ArchiveThreadRow row) return;
            ResourceId id = row.Summary.Id;
            if (e.KeyModifiers == KeyModifiers.Shift) { if (OpenGallery != null) await OpenGallery(id, null, list); }
            else if (FrozenArchiveSelection().Length != 1) Message(Texts.T("archive.command.singleRead"));
            else { OpenReaderRequested?.Invoke(list); await LoadThreadAsync(id); }
            return;
        }
        string? action = MatchesArchiveCommand(e, "rules.edit-scope") ? "rules"
            : MatchesArchiveCommand(e, "location.open-folder") ? "folder"
            : MatchesArchiveCommand(e, "location.open-source") ? "browser" : null;
        if (action != null && ArchiveLead is ArchiveThreadRow lead)
        {
            e.Handled = true;
            if (action == "rules" && CanRemoveWatch?.Invoke(lead.Summary.Id) != true) Message(Texts.T("archive.command.noWatch"));
            else if (action == "folder" && string.IsNullOrWhiteSpace(lead.Summary.MediaDirectory)) Message(Texts.T("archive.command.noFolder"));
            else if (action == "browser" && string.IsNullOrWhiteSpace(lead.Summary.Url)) Message(Texts.T("archive.command.noSource"));
            else RunArchiveLeadAction(lead, action);
        }
    }

    private Task CopyArchiveSelectionAsync()
    {
        string[] urls = ArchiveVisibleRows.Where(row => _archiveSelectionIds.Contains(row.Summary.Id)).Select(row => row.Summary.Url).ToArray();
        return CopySelectedUrls?.Invoke(urls) ?? Task.CompletedTask;
    }
    private Task RemoveArchiveSelectionAsync()
    {
        ResourceId[] ids = FrozenArchiveSelection().Where(id => CanRemoveWatch?.Invoke(id) == true).ToArray();
        return ids.Length > 0 ? RemoveSelectedWatches?.Invoke(ids) ?? Task.CompletedTask : Task.CompletedTask;
    }
    private async void OnArchiveRemoveSelection(object? sender, RoutedEventArgs e) => await RemoveArchiveSelectionAsync();
    private async void OnArchiveCopySelection(object? sender, RoutedEventArgs e) => await CopyArchiveSelectionAsync();
    private async void OnArchiveCheckSelection(object? sender, RoutedEventArgs e)
    { if (CheckSelectedWatches != null) await CheckArchiveSelectionAsync(false); }
    private async void OnArchiveRetrySelection(object? sender, RoutedEventArgs e)
    { if (CheckSelectedWatches != null) await CheckArchiveSelectionAsync(true); }
    private async void OnArchiveSelectionDetails(object? sender, RoutedEventArgs e)
    { if (ArchiveLead is ArchiveThreadRow row) await ShowArchiveDetailsAsync(row); }
    private void OnArchiveLeadAction(object? sender, RoutedEventArgs e)
    { if (ArchiveLead is ArchiveThreadRow row && sender is MenuItem { CommandParameter: string action }) RunArchiveLeadAction(row, action); }
}
