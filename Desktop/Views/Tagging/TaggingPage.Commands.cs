using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;
using Ursa.Controls;

namespace MeowChan;

// The commands of the tagging page: the toolbar (Add…, Remove finished, Clear queue…), the list's context menu and keys (catalog
// commands of the "tagging" context: Enter continues here, Delete removes, Alt+Up/Down moves) and the clear question. Each one is a
// single command of TaggingQueueController; the page then shows the committed queue and the sentence it left.
public sealed partial class TaggingPage
{
    private static readonly string[] RowCommands =
    {
        "tagging.continue-here", "tagging.mark-done", "tagging.mark-open", "tagging.mark-skipped",
        "tagging.move-up", "tagging.move-down", "tagging.remove"
    };

    private readonly Button _add = new() { Name = "TaggingAdd" };
    private readonly Button _clearDone = new() { Name = "TaggingClearDone" };
    private readonly Button _clear = new() { Name = "TaggingClear" };
    private readonly Border _clearConfirm = new() { Name = "TaggingClearConfirm", Classes = { "workspace-warning" }, IsVisible = false };
    private readonly TextBlock _clearQuestion = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _clearYes = new() { Name = "TaggingClearYes", Classes = { "danger" } };
    private readonly Button _clearNo = new() { Name = "TaggingClearNo" };
    private readonly ContextMenu _menu = new() { Name = "TaggingListMenu" };
    private readonly Dictionary<string, MenuItem> _menuItems = new(StringComparer.Ordinal);

    internal ContextMenu ListMenu => _menu;
    internal bool ClearQuestionOpen => _clearConfirm.IsVisible;
    internal Button ClearButton => _clear;
    internal Button ClearYesButton => _clearYes;
    internal Button ClearDoneButton => _clearDone;

    private void InitializeCommands()
    {
        _add.Command = new DelegateCommand(() => AddRequested?.Invoke(), () => !_queueWork.Ended && AddRequested != null);
        _clearDone.Command = new DelegateCommand(() => _ = ClearFinishedAsync(), () => CanChange && _queueWork.Queue.Done > 0);
        _clear.Command = new DelegateCommand(AskClear, () => !_queueWork.Ended && !_queueWork.Busy && !_queueWork.Loading && _queueWork.Loaded
            && (_queueWork.Queue.Count > 0 && _queueWork.Queue.CanWrite || _queueWork.LoadError != null));
        foreach (Button button in new[] { _add, _clearDone, _clear })
        {
            ToolBar.SetOverflowMode(button, OverflowMode.AsNeeded);
            _actions.Items.Add(button);
        }
        _clearYes.Click += async (_, _) => await ClearAllAsync();
        _clearNo.Click += (_, _) => CloseClearQuestion(focusClear: true);
        _clearConfirm.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseClearQuestion(focusClear: true); } };

        foreach (string id in RowCommands)
        {
            string command = id;
            MenuItem item = CommandBindings.CreateMenuItem(command, () => _ = ExecuteRowCommandAsync(command), () => CanRowCommand(command));
            _menuItems[command] = item;
            _menu.Items.Add(item);
            if (command is "tagging.continue-here" or "tagging.mark-skipped" or "tagging.move-down") _menu.Items.Add(new Separator());
        }
        _menu.Opening += (_, _) => { foreach (MenuItem item in _menuItems.Values) CommandBindings.Refresh(item); };
        _list.ContextMenu = _menu;
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        _list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null && CanRowCommand("tagging.continue-here"))
                _ = ExecuteRowCommandAsync("tagging.continue-here");
        };
        _list.SelectionChanged += (_, _) => RefreshCommandStates();
    }

    private Border BuildClearConfirmation()
    {
        WorkViewElement.SetId(_clearConfirm, "tagging.clear-confirm");
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _clearYes, _clearNo } };
        _clearConfirm.Child = new StackPanel { Spacing = 8, Children = { _clearQuestion, buttons } };
        return _clearConfirm;
    }

    private void LocalizeCommands()
    {
        CommandBindings.Apply(_add, "tagging.add");
        CommandBindings.Apply(_clearDone, "tagging.clear-done");
        CommandBindings.Apply(_clear, "tagging.clear");
        _clearYes.Content = Texts.T("tagging.page.clear.yes");
        _clearNo.Content = Texts.T("queue.ask.no");
        if (_clearConfirm.IsVisible) _clearQuestion.Text = Texts.T("tagging.page.clear.ask", _queueWork.Queue.Count);
        _keysHint.Text = Texts.T("tagging.page.keys", Shortcut("tagging.continue-here"), Shortcut("tagging.remove"),
            Shortcut("tagging.move-up"), Shortcut("tagging.move-down"));
        _actions.RefreshOverflowLabel();
    }

    private static string Shortcut(string id) => CommandCatalog.ShortcutDisplay(id) ?? CommandCatalog.Label(id);

    private void RefreshCommandStates()
    {
        foreach (Button button in new[] { _add, _clearDone, _clear })
            if (button.Command is DelegateCommand command) { button.IsEnabled = command.CanExecute(null); command.RaiseCanExecuteChanged(); }
        foreach (MenuItem item in _menuItems.Values) CommandBindings.Refresh(item);
    }

    private TaggingQueueRow? Lead => _list.SelectedItem as TaggingQueueRow
        ?? _list.SelectedItems?.OfType<TaggingQueueRow>().FirstOrDefault();

    private IReadOnlyList<TaggingQueueRow> Selected => _list.SelectedItems?.OfType<TaggingQueueRow>().OrderBy(row => row.Index).ToArray()
        ?? (IReadOnlyList<TaggingQueueRow>)Array.Empty<TaggingQueueRow>();

    internal bool CanRowCommand(string id)
    {
        if (!CanChange || Lead is not { } lead) return false;
        TagQueueSnapshot queue = _queueWork.Queue;
        return id switch
        {
            "tagging.continue-here" => StartTagging != null && lead.Item.State == TagQueueState.Open,
            "tagging.mark-done" => Selected.Any(row => row.Item.State != TagQueueState.Done),
            "tagging.mark-open" => Selected.Any(row => row.Item.State != TagQueueState.Open),
            "tagging.mark-skipped" => Selected.Any(row => row.Item.State != TagQueueState.Skipped),
            "tagging.move-up" => TaggingQueueView.MoveTarget(queue, Filter, lead.Key, -1) != null,
            "tagging.move-down" => TaggingQueueView.MoveTarget(queue, Filter, lead.Key, 1) != null,
            "tagging.remove" => true,
            _ => false
        };
    }

    /// <summary>Runs a row command on the selection (the lead row for continue and move); refused silently when it does not apply.</summary>
    internal Task ExecuteRowCommandAsync(string id)
    {
        // T1: a double-click or Enter on a finished or skipped row says why nothing starts (only open entries can be continued from).
        if (id == "tagging.continue-here" && StartTagging != null && CanChange && Lead is { Item.State: not TagQueueState.Open })
        {
            ShowStatus(Texts.T("tagging.page.continue.notOpen"));
            return Task.CompletedTask;
        }
        if (!CanRowCommand(id) || Lead is not { } lead) return Task.CompletedTask;
        IReadOnlyList<TaggingQueueRow> selected = Selected;
        TagQueueKey[] keys = selected.Select(row => row.Key).ToArray();
        return id switch
        {
            "tagging.continue-here" => StartAsync(lead.Index),
            "tagging.mark-done" => AfterAsync(_queueWork.SetStatesAsync(keys, TagQueueState.Done)),
            "tagging.mark-open" => AfterAsync(_queueWork.SetStatesAsync(keys, TagQueueState.Open)),
            "tagging.mark-skipped" => AfterAsync(_queueWork.SetStatesAsync(keys, TagQueueState.Skipped)),
            "tagging.move-up" => AfterAsync(_queueWork.MoveAsync(lead.Key, Filter, -1), select: lead.Key),
            "tagging.move-down" => AfterAsync(_queueWork.MoveAsync(lead.Key, Filter, 1), select: lead.Key),
            "tagging.remove" => AfterAsync(_queueWork.RemoveAsync(keys), select: RowAfter(selected)?.Key),
            _ => Task.CompletedTask
        };
    }

    // The row after the last removed one (else the one before the first) keeps the keyboard where the user was.
    private TaggingQueueRow? RowAfter(IReadOnlyList<TaggingQueueRow> removed)
    {
        if (removed.Count == 0) return null;
        HashSet<TagQueueKey> gone = removed.Select(row => row.Key).ToHashSet();
        return _rows.FirstOrDefault(row => row.Index > removed[^1].Index && !gone.Contains(row.Key))
            ?? _rows.LastOrDefault(row => row.Index < removed[0].Index);
    }

    // List keys come from the catalog (rebindable, listed in the shortcut editor). The list itself keeps arrows, Home/End and
    // type-ahead; a key that matches a command is consumed even when the command does not apply right now.
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _queueWork.Ended || ShortcutService.IsCapturing || !MainWindow.IsListCommandSource(e.Source, _list)) return;
        foreach (string id in RowCommands)
        {
            if (!ShortcutService.Current.Matches(e, id)) continue;
            e.Handled = true;
            _ = ExecuteRowCommandAsync(id);
            return;
        }
    }

    /// <summary>Moves the cursor to <paramref name="index"/> (an entry the user picked) and hands over to the gallery.</summary>
    internal async Task StartAsync(int? index)
    {
        if (StartTagging is not { } start || _queueWork.Ended) return;
        if (index is { } at)
        {
            if (!CanChange) return;
            Task<bool> moved = _queueWork.SetCursorAsync(at);
            UpdateActions();
            bool done = await moved;
            Show();
            if (!done) { ShowStatus(_queueWork.Status); return; }
        }
        else if (!CanStart) return;
        await start();
    }

    internal Task ClearFinishedAsync() => AfterAsync(_queueWork.ClearFinishedAsync());

    private void AskClear()
    {
        if (_queueWork.Ended || _queueWork.Busy) return;
        _clearQuestion.Text = Texts.T("tagging.page.clear.ask", _queueWork.Queue.Count);
        WorkViewGate.Apply(_clearConfirm, _workView, stateVisible: true);
        _clearNo.Focus();
    }

    private void CloseClearQuestion(bool focusClear)
    {
        WorkViewGate.Apply(_clearConfirm, _workView, stateVisible: false);
        if (!focusClear) return;
        if (_clear.IsEffectivelyVisible) _clear.Focus();
        else FocusContent();
    }

    /// <summary>Empties the queue. A damaged file is kept next to its place and replaced by an empty queue (the store's recovery).</summary>
    internal async Task ClearAllAsync()
    {
        CloseClearQuestion(focusClear: false);
        await AfterAsync(_queueWork.ClearAllAsync());
        FocusContent();
    }

    /// <summary>Waits for one controller command, then shows the queue it committed (or kept) and the sentence it left.</summary>
    private async Task AfterAsync(Task<bool> command, TagQueueKey? select = null)
    {
        bool hadFocus = _list.IsKeyboardFocusWithin;
        UpdateActions();
        bool done = await command;
        if (_queueWork.Ended) return;
        Show();
        ShowStatus(_queueWork.Status);
        if (done && select is { } key) SelectOnly(key, hadFocus);
        else if (hadFocus && !_list.IsKeyboardFocusWithin) FocusContent();
    }

    // The entry to show selected once the list holds it (the page was built or is still loading when the request comes).
    private TagQueueKey? _pendingSelect;

    /// <summary>
    /// Selects the row of <paramref name="key"/>, brings it into view and puts the keyboard on it (QW6: the way back from the gallery
    /// marks the medium shown last). A filter that hides the row is set back to "All entries"; before the queue is loaded the request
    /// waits for it. False when the page has ended.
    /// </summary>
    internal bool SelectKey(TagQueueKey key)
    {
        if (_queueWork.Ended) return false;
        _pendingSelect = key;
        Refresh();
        ApplyPendingSelect();
        return true;
    }

    private void ApplyPendingSelect()
    {
        if (_pendingSelect is not { } key || _queueWork.Ended || !_queueWork.Loaded) return;
        _pendingSelect = null;
        if (!_queueWork.Queue.Contains(key)) return; // removed meanwhile: nothing to mark
        if (_rows.All(row => row.Key != key) && Filter != TaggingQueueFilter.All) Filter = TaggingQueueFilter.All; // re-renders the rows
        SelectOnly(key, focus: false);
        // The list is laid out after this turn (the page may just have become the shown one): scroll and focus then.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_queueWork.Ended && IsEffectivelyVisible) SelectOnly(key, focus: true);
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void SelectOnly(TagQueueKey key, bool focus)
    {
        if (_rows.FirstOrDefault(row => row.Key == key) is not { } row) return;
        _list.SelectedItems?.Clear();
        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
        if (focus && _list.ContainerFromItem(row) is Control container) container.Focus();
    }
}
