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
using MeowChan.Desktop.Controls;
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
    private readonly Button _add = new() { Name = "TaggingAdd" };
    private readonly Button _clearDone = new() { Name = "TaggingClearDone" };
    private readonly Button _clear = new() { Name = "TaggingClear" };
    private readonly Border _clearConfirm = new() { Name = "TaggingClearConfirm", Classes = { "workspace-warning" }, IsVisible = false };
    private readonly TextBlock _clearQuestion = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _clearYes = new() { Name = "TaggingClearYes", Classes = { "danger" } };
    private readonly Button _clearNo = new() { Name = "TaggingClearNo" };
    private ContextMenuHost? _menuHost;
    private ListBoxItem? _spaceRow;

    internal bool ClearQuestionOpen => _clearConfirm.IsVisible;
    internal Button ClearButton => _clear;
    internal Button ClearYesButton => _clearYes;
    internal Button ClearDoneButton => _clearDone;
    internal Button AddButton => _add;
    internal HelpButton HelpButton => _help;
    internal ActionToolBar Actions => _actions;

    // The seams of the commands that leave the page (the shell wires them; a page without it has none and those entries are off).
    /// <summary>Opens the gallery over these entries (queue order) starting at the given index of the list.</summary>
    internal Func<IReadOnlyList<TagQueueItem>, int, Task>? OpenGallery { get; set; }
    /// <summary>Shows the entry's post in its thread (the archive reader).</summary>
    internal Func<TagQueueItem, Task>? ShowInThread { get; set; }
    /// <summary>Opens the media page on the entry.</summary>
    internal Func<TagQueueItem, Task>? ShowInMedia { get; set; }
    /// <summary>Opens the folder of the entry's file.</summary>
    internal Func<TagQueueItem, Task>? ShowInFolder { get; set; }
    /// <summary>Copies the path of the entry's file.</summary>
    internal Func<TagQueueItem, Task>? CopyPath { get; set; }
    /// <summary>
    /// The entries the page works on besides the state filter: the one place a later narrowing (a group) plugs in. Moves, the list
    /// and every command read it; the numbers and the walk marker stay those of the whole queue.
    /// </summary>
    internal Func<TagQueueItem, bool>? Scope { get; set; }

    private void InitializeCommands()
    {
        _add.Command = new DelegateCommand(() => AddRequested?.Invoke(), () => !_queueWork.Ended && AddRequested != null);
        _clearDone.Command = new DelegateCommand(() => _ = ClearFinishedAsync(), () => CanChange && _queueWork.Queue.Done > 0);
        _clear.Command = new DelegateCommand(AskClear, () => !_queueWork.Ended && !_queueWork.Busy && !_queueWork.Loading && _queueWork.Loaded
            && (_queueWork.Queue.Count > 0 && _queueWork.Queue.CanWrite || _queueWork.LoadError != null));
        // Queue actions stay together; visible-result controls have their own measured row above the list.
        foreach (Control item in new Control[] { _add, _help, _groupManage, _clearDone, _clear })
        {
            ToolBar.SetOverflowMode(item, OverflowMode.AsNeeded);
            _actions.Items.Add(item);
        }
        _listControls.Children.Add(_filterPanel);
        _listControls.Children.Add(_listPresentation);
        _listControls.TrailingChild = _listPresentation;
        _listControls.SetTrailingCompact = compact =>
            _viewToggle.SetLabelsVisible(!compact || ToolBar.GetOverflowMode(_viewToggle) == OverflowMode.Always);
        foreach (Control item in new Control[] { _columnsButton, _viewToggle })
        {
            ToolBar.SetOverflowMode(item, OverflowMode.AsNeeded);
            _listPresentation.Items.Add(item);
        }
        // The error banner's "Retry" reads the queue file again (RetryLoadAsync is a no-op unless the page may reload now).
        _retryLoad.Click += (_, _) => OpenActions.Start(RetryLoadAsync, "tagging.retry");
        _clearYes.Click += async (_, _) => await ClearAllAsync();
        _clearNo.Click += (_, _) => CloseClearQuestion(focusClear: true);
        _clearConfirm.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseClearQuestion(focusClear: true); } };

        _menuHost = new ContextMenuHost(_list, ComposeRowMenu, fallbackFocus: () => FocusContent());
        _list.AddHandler(PointerPressedEvent, OnContextPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnResultSpaceUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        // A double-click on an entry runs "Continue here" like Enter; one that cannot (a finished entry, a busy queue) says why in the status line
        // instead of doing nothing (the gate used to sit here, so the sentence of ExecuteRowCommandAsync was never reached).
        _list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null)
                OpenActions.Start(() => ExecuteRowCommandAsync("tagging.continue-here", explain: true), "tagging.list.doubleClick");
        };
        _list.SelectionChanged += OnSelectionChanged;
        InstallMiddleClick(_list);
    }

    // Dead-affordance audit: the middle click on an entry runs "Show in folder", as on the Media page's tiles and the other lists. It acts on the
    // entry that was clicked (the selection stays as it is), with the menu entry's own rules.
    private readonly MiddleClickTracker _middleClick = new();

    private void OnContextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox surface || !e.GetCurrentPoint(surface).Properties.IsRightButtonPressed
            || !MainWindow.IsListCommandSource(e.Source, surface)) return;
        ListBoxItem? item = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(candidate => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(candidate), surface));
        if (item?.DataContext is not TaggingQueueRow row) return;
        TaggingQueueRow[] selected = surface.SelectedItems?.OfType<TaggingQueueRow>().ToArray() ?? Array.Empty<TaggingQueueRow>();
        if (!selected.Contains(row)) surface.SelectedItem = row;
        else if (!ReferenceEquals(surface.SelectedItem, row))
        {
            surface.SelectedItem = row;
            foreach (TaggingQueueRow kept in selected.Where(kept => !ReferenceEquals(kept, row))) surface.SelectedItems?.Add(kept);
        }
        item.Focus();
    }

    private void InstallMiddleClick(ListBox surface)
    {
        surface.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!MiddleClickTracker.IsPress(e, surface)) { _middleClick.Cancel(); return; }
            _middleClick.Pressed(MiddleClickTracker.RowAt<TaggingQueueRow>(e.Source, surface));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        surface.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.Handled || !MiddleClickTracker.IsRelease(e)) return;
            TaggingQueueRow? row = MiddleClickTracker.RowAt<TaggingQueueRow>(e.Source, surface);
            if (!_middleClick.Released(row) || row == null) return;
            e.Handled = true;
            OpenActions.Start(() => ShowInFolderOfAsync(row), "tagging.middleClick");
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>"Show in folder" for one entry, with the rules of the menu entry for a selection of just that entry; refused silently when it does not apply.</summary>
    internal Task ShowInFolderOfAsync(TaggingQueueRow row)
    {
        if (_queueWork.Ended) return Task.CompletedTask;
        TaggingCommandContext context = new(_queueWork.Queue, Filter, new[] { row }, row, Capabilities, _effectiveScope);
        return TaggingRowCommands.Evaluate(TaggingCommands.ShowInFolder, context).Available ? Leaving(ShowInFolder, row.Item) : Task.CompletedTask;
    }

    /// <summary>The context menu of the selected entries (null without a selection); composed fresh at every opening.</summary>
    private ContextMenuBuilder? ComposeRowMenu() =>
        Lead == null ? null : TaggingQueueMenu.Compose(CommandContext(), id => () => OpenActions.Start(() => ExecuteRowCommandAsync(id), "tagging.menu"));

    /// <summary>The menu as plain data (smoke and test seam); null without a selection.</summary>
    internal ContextMenuModel? ListMenuModel() => ComposeRowMenu()?.ToModel();

    /// <summary>Opens the context menu on the list (the keyboard way: under the list).</summary>
    internal void ShowListMenu() => ActiveMenuHost?.Show(atPointer: false);
    internal ContextMenu? OpenListMenu => ActiveMenuHost?.OpenMenu;

    private TaggingCapabilities Capabilities => new(CanChange, StartTagging != null && CanChange, CanTag, _queueWork.StampNames.Count > 0,
        OpenGallery != null, ShowInThread != null, ShowInMedia != null, ShowInFolder != null, CopyPath != null);

    private TaggingCommandContext CommandContext() => new(_queueWork.Queue, Filter, Selected, Lead, Capabilities, _effectiveScope);

    private Border BuildClearConfirmation()
    {
        WorkViewElement.SetId(_clearConfirm, "tagging.clear-confirm");
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _clearYes, _clearNo } };
        _clearConfirm.Child = new StackPanel { Spacing = 8, Children = { _clearQuestion, buttons } };
        return _clearConfirm;
    }

    private void LocalizeCommands()
    {
        ApplyToolbarIcons();
        _clearYes.Content = Texts.T("tagging.page.clear.yes");
        _clearNo.Content = Texts.T("queue.ask.no");
        if (_clearConfirm.IsVisible) _clearQuestion.Text = Texts.T("tagging.page.clear.ask", _queueWork.Queue.Count);
        _help.Source = () =>
        {
            HelpContent content = HelpContent.FromKeys("help.taggingPage");
            return content with { Paragraphs = content.Paragraphs
                .Append(Texts.T("tagging.strip.browseHint", Shortcut("tags.queue.done-next")))
                .Append(Texts.T("tagging.page.keys",
                Shortcut("tagging.continue-here"), Shortcut("tagging.remove"),
                Shortcut("tagging.move-up"), Shortcut("tagging.move-down"))).ToArray() };
        };
        _actions.RefreshOverflowLabel();
        _listPresentation.RefreshOverflowLabel();
    }

    // "Add…" and "Remove finished" always show their icon with their word (U06: removing the finished entries is a named action, not a
    // bare icon; the bar already moves it under "More" when room runs out). "Clear queue…" is rare and destructive: icon-only at the end
    // of the bar (label and key in tooltip and accessible name); a work view that moves it under "More" gives it its icon with the label.
    private void ApplyToolbarIcons()
    {
        CommandBindings.Apply(_add, "tagging.add", iconWithLabel: true);
        CommandBindings.Apply(_clearDone, "tagging.clear-done", iconWithLabel: true);
        _clearDone.Classes.Set(WorkspaceIcons.SquareClass, false);
        {
            bool reduced = ToolBar.GetOverflowMode(_clear) == OverflowMode.Always;
            CommandBindings.Apply(_clear, "tagging.clear", iconOnly: !reduced, iconWithLabel: reduced);
            // Geometry is local to this page; labelled overflow presentations must not retain an icon-only width.
            _clear.Classes.Set(WorkspaceIcons.SquareClass, !reduced);
        }
    }

    private static string Shortcut(string id) => CommandCatalog.ShortcutDisplay(id) ?? CommandCatalog.Label(id);

    private void RefreshCommandStates()
    {
        foreach (Button button in new[] { _add, _clearDone, _clear })
            if (button.Command is DelegateCommand command) { button.IsEnabled = command.CanExecute(null); command.RaiseCanExecuteChanged(); }
    }

    private TaggingQueueRow? Lead => ActiveList.SelectedItem as TaggingQueueRow
        ?? ActiveList.SelectedItems?.OfType<TaggingQueueRow>().FirstOrDefault();

    private IReadOnlyList<TaggingQueueRow> Selected => ActiveList.SelectedItems?.OfType<TaggingQueueRow>().OrderBy(row => row.Index).ToArray()
        ?? (IReadOnlyList<TaggingQueueRow>)Array.Empty<TaggingQueueRow>();

    internal bool CanRowCommand(string id) => TaggingRowCommands.Evaluate(id, CommandContext()).Available;

    /// <summary>
    /// Runs a command on the selection (the lead entry for continue and the ones that open something). A command that does not apply is
    /// refused; with <paramref name="explain"/> (a key or a double-click, which have no disabled entry to carry the reason) the status line says why.
    /// </summary>
    internal Task ExecuteRowCommandAsync(string id, bool explain = false)
    {
        // T1: a double-click or Enter on a finished or skipped row says why nothing starts (only open entries can be continued from).
        if (id == TaggingCommands.ContinueHere && StartTagging != null && CanChange && Lead is { Item.State: not TagQueueState.Open })
        {
            ShowStatus(Texts.T("tagging.page.continue.notOpen"));
            return Task.CompletedTask;
        }
        if (id is TaggingCommands.GroupNew or TaggingCommands.GroupRemove || id.StartsWith(TaggingCommands.GroupPrefix, StringComparison.Ordinal))
        {
            if (SelectedCount == 0 || !CanChange) return Task.CompletedTask;
            if (id == TaggingCommands.GroupNew) return CreateGroupDialogAsync();
            if (id == TaggingCommands.GroupRemove) return AssignSelectionToGroupAsync(null);
            string groupId = id[TaggingCommands.GroupPrefix.Length..];
            return Queue.Group(groupId) != null ? AssignSelectionToGroupAsync(groupId) : Task.CompletedTask;
        }
        if (!CanRowCommand(id) || Lead is not { } lead)
        {
            if (explain && !_queueWork.Ended && TaggingRowCommands.Evaluate(id, CommandContext()).Reason is { Length: > 0 } reason) ShowStatus(reason);
            return Task.CompletedTask;
        }
        IReadOnlyList<TaggingQueueRow> selected = Selected;
        TagQueueKey[] keys = selected.Select(row => row.Key).ToArray();
        return id switch
        {
            TaggingCommands.ContinueHere => StartAsync(lead.Index),
            TaggingCommands.Tag or TaggingCommands.TagTogether => TagSelectionAsync(),
            TaggingCommands.ApplyStamp => ApplyStampToSelectionAsync(),
            TaggingCommands.MarkDone => AfterAsync(_queueWork.SetStatesAsync(keys, TagQueueState.Done)),
            TaggingCommands.MarkOpen => AfterAsync(_queueWork.SetStatesAsync(keys, TagQueueState.Open)),
            TaggingCommands.MarkSkipped => AfterAsync(_queueWork.SetStatesAsync(keys, TagQueueState.Skipped)),
            TaggingCommands.OpenGallery => OpenGalleryAsync(selected, lead),
            TaggingCommands.ShowInThread => Leaving(ShowInThread, lead.Item),
            TaggingCommands.ShowInMedia => Leaving(ShowInMedia, lead.Item),
            TaggingCommands.ShowInFolder => Leaving(ShowInFolder, lead.Item),
            TaggingCommands.CopyPath => Leaving(CopyPath, lead.Item),
            TaggingCommands.MoveUp => AfterAsync(_queueWork.MoveAsync(lead.Key, Filter, -1, _effectiveScope), select: lead.Key),
            TaggingCommands.MoveDown => AfterAsync(_queueWork.MoveAsync(lead.Key, Filter, 1, _effectiveScope), select: lead.Key),
            TaggingCommands.MoveFirst => AfterAsync(_queueWork.MoveToEdgeAsync(lead.Key, toEnd: false), select: lead.Key),
            TaggingCommands.MoveLast => AfterAsync(_queueWork.MoveToEdgeAsync(lead.Key, toEnd: true), select: lead.Key),
            TaggingCommands.Remove => AfterAsync(_queueWork.RemoveAsync(keys), select: RowAfter(selected)?.Key),
            _ => Task.CompletedTask
        };
    }

    private static Task Leaving(Func<TagQueueItem, Task>? action, TagQueueItem item) => action == null ? Task.CompletedTask : action(item);

    // One selected entry opens the gallery over everything shown (the way into a browse), several open it over exactly the selection.
    private Task OpenGalleryAsync(IReadOnlyList<TaggingQueueRow> selected, TaggingQueueRow lead)
    {
        if (OpenGallery is not { } open) return Task.CompletedTask;
        TagQueueItem[] window = (selected.Count > 1 ? selected : _rows).Select(row => row.Item).ToArray();
        int start = Array.FindIndex(window, item => item.Key == lead.Key);
        return start < 0 ? Task.CompletedTask : open(window, start);
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
        if (e.Handled || _queueWork.Ended || ShortcutService.IsCapturing || !MainWindow.IsListCommandSource(e.Source, ActiveList)) return;
        // Esc (fixed) clears a selection before anything else; without one the key is left to the window.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && SelectedCount > 0)
        {
            e.Handled = true;
            ClearSelection();
            return;
        }
        // R: "Rate and tag…" on the entry the key came from (or the selection), as on the media page; before, the key did nothing on this page.
        if (ShortcutService.Current.Matches(e, "media.rating.edit"))
        {
            e.Handled = true;
            ListBoxItem? item = ResultItem(e.Source);
            if (item?.DataContext is TaggingQueueRow row && !ActiveList.Selection.SelectedItems.Contains(row)) ActiveList.SelectedItem = row;
            OpenActions.Start(() => RateAndTagByKeyAsync(item), "tagging.list.rateAndTag");
            return;
        }
        foreach (string id in TaggingCommands.All)
        {
            if (!ShortcutService.Current.Matches(e, id)) continue;
            e.Handled = true;
            // The Tag key over several entries is "Tag together…" (the entry of one medium is off for a selection).
            string command = id == TaggingCommands.Tag && Selected.Count > 1 ? TaggingCommands.TagTogether : id;
            if (id == TaggingCommands.ContinueHere && Lead == null && ResultItem(e.Source)?.DataContext is TaggingQueueRow focused)
            {
                if (focused.Item.State == TagQueueState.Open)
                    OpenActions.Start(() => StartAsync(focused.Index), "tagging.list.key");
                else ShowStatus(Texts.T("tagging.page.continue.notOpen"));
            }
            else OpenActions.Start(() => ExecuteRowCommandAsync(command, explain: true), "tagging.list.key");
            return;
        }
        // Claim only plain Space on a focused item, after all configurable shortcuts.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && ResultItem(e.Source) is { IsFocused: true } result)
        {
            if (MeowChan.Desktop.MediaActions.MediaMenuPresenter.IsContextKey(e)
                || CommandBindings.RouteVisibleActions(this, e)) return;
            _spaceRow = result;
            e.Handled = true;
        }
    }

    private ListBoxItem? ResultItem(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(candidate => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(candidate), ActiveList));

    private void OnResultSpaceUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        ListBoxItem? pressed = _spaceRow;
        _spaceRow = null;
        if (e.Handled || pressed == null || e.KeyModifiers != KeyModifiers.None || !pressed.IsFocused
            || ShortcutService.IsCapturing || !ReferenceEquals(ResultItem(e.Source), pressed)
            || !MainWindow.IsListCommandSource(e.Source, ActiveList)) return;

        int index = ActiveList.IndexFromContainer(pressed);
        if (index < 0) return;
        e.Handled = true;
        if (ActiveList.Selection.IsSelected(index)) ActiveList.Selection.Deselect(index);
        else ActiveList.Selection.Select(index);
    }

    /// <summary>Moves the cursor to <paramref name="index"/> (an entry the user picked) and hands over to the gallery.</summary>
    internal async Task StartAsync(int? index)
    {
        if (StartTagging is not { } start || _queueWork.Ended) return;
        int? target = index ?? TaggingQueueView.Resume(Queue, _selectedGroupId, _ungroupedSelected, _effectiveScope);
        if (target is not { } position || position < 0 || position >= Queue.Count) return;
        TaggingWalkScope walk = new(_selectedGroupId, _ungroupedSelected, _effectiveScope, Queue.Items[position].Key);
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
        await start(walk);
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
        bool hadFocus = ActiveList.IsKeyboardFocusWithin || _selectionBar.IsKeyboardFocusWithin;
        UpdateActions();
        bool done = await command;
        if (_queueWork.Ended) return;
        Show();
        ShowStatus(_queueWork.Status);
        if (done && select is { } key) SelectOnly(key, hadFocus);
        else if (hadFocus && !ActiveList.IsKeyboardFocusWithin) FocusContent();
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
        ActiveList.SelectedItems?.Clear();
        ActiveList.SelectedItem = row;
        ActiveList.ScrollIntoView(row);
        if (focus && ActiveList.ContainerFromItem(row) is Control container) container.Focus();
    }
}
