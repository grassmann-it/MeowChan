using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan;

// The selection bar of the tagging page (TG1): one line above the list or grid while at least one entry is selected, the toolbar above it
// stands still. The count is a polite live region; the actions are the context menu's commands for the selection (Tag… for one entry,
// Tag together… for several, Apply tag stamp, State, Remove) and act on the entries selected at the moment of the click. Esc or the
// cross clears the selection. What the work view reduces moves under the bar's own "More".
public sealed partial class TaggingPage
{
    private readonly Border _selectionBar = new() { Name = "TaggingSelectionBar", Classes = { "workspace-info" }, IsVisible = false, Padding = new Thickness(12, 4),
        Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _selectionText = new() { Name = "TaggingSelectionStatus", Classes = { "tnum" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ActionToolBar _selectionActions = new() { Name = "TaggingSelectionActions", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _selTag = new() { Name = "TaggingSelectionTag" };
    private readonly Button _selStamp = new() { Name = "TaggingSelectionStamp" };
    private readonly Button _selDone = new() { Name = "TaggingSelectionDone" };
    private readonly Button _selSkipped = new() { Name = "TaggingSelectionSkipped" };
    private readonly Button _selState = new() { Name = "TaggingSelectionState" };
    private readonly Button _selContinue = new() { Name = "TaggingSelectionContinue" };
    private readonly Button _selGroup = new() { Name = "TaggingSelectionGroup" };
    private readonly Button _selRemove = new() { Name = "TaggingSelectionRemove" };
    private readonly TextBlock _selStateText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _selGroupText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _selClear = new() { Name = "TaggingSelectionClear" };
    private ContextMenuHost? _stateMenuHost;
    private ContextMenuHost? _groupMenuHost;

    internal Border SelectionBar => _selectionBar;
    internal TextBlock SelectionText => _selectionText;
    internal ActionToolBar SelectionActions => _selectionActions;
    internal Button SelectionTagButton => _selTag;
    internal Button SelectionStampButton => _selStamp;
    internal Button SelectionDoneButton => _selDone;
    internal Button SelectionSkippedButton => _selSkipped;
    internal Button SelectionStateButton => _selState;
    internal Button SelectionContinueButton => _selContinue;
    internal Button SelectionGroupButton => _selGroup;
    internal Button SelectionRemoveButton => _selRemove;
    internal Button SelectionClearButton => _selClear;

    private Control BuildSelectionBar()
    {
        AutomationProperties.SetLiveSetting(_selectionText, AutomationLiveSetting.Polite);
        _selectionActions.PrioritizedOverflow = true;
        (Button Button, int Group)[] actions =
        [
            (_selTag, 0), (_selStamp, 0),
            (_selDone, 1), (_selSkipped, 1), (_selState, 1), (_selContinue, 1),
            (_selGroup, 2), (_selRemove, 2),
        ];
        foreach ((Button button, int group) in actions)
        {
            ToolBar.SetOverflowMode(button, OverflowMode.AsNeeded);
            ActionToolBar.SetGroup(button, group);
            // Outlined like the selection row of the media page, so a disabled action still looks like a button (WorkspaceStyles.axaml;
            // the outline only applies on the bar itself, an item under "More" keeps the popup's plain rows).
            button.Classes.Add("bar-button");
            _selectionActions.Items.Add(button);
        }
        _selRemove.Classes.Add("danger");
        _selTag.Click += (_, _) => _ = ExecuteSelectionAsync(Selected.Count > 1 ? TaggingCommands.TagTogether : TaggingCommands.Tag, _selTag);
        _selStamp.Click += (_, _) => OpenActions.Start(() => ExecuteRowCommandAsync(TaggingCommands.ApplyStamp), "tagging.selection.stamp");
        _selDone.Click += (_, _) => OpenActions.Start(() => ExecuteRowCommandAsync(TaggingCommands.MarkDone), "tagging.selection.done");
        _selSkipped.Click += (_, _) => OpenActions.Start(() => ExecuteRowCommandAsync(TaggingCommands.MarkSkipped), "tagging.selection.skipped");
        _selRemove.Click += (_, _) => OpenActions.Start(() => ExecuteRowCommandAsync(TaggingCommands.Remove), "tagging.selection.remove");
        _selState.Click += (_, _) => _stateMenuHost?.Show(atPointer: false);
        _selContinue.Click += (_, _) => OpenActions.Start(() => ExecuteRowCommandAsync(TaggingCommands.ContinueHere, explain: true), "tagging.selection.continue");
        _selGroup.Click += (_, _) => _groupMenuHost?.Show(atPointer: false);
        _stateMenuHost = new ContextMenuHost(_selState, ComposeStateMenu, fallbackFocus: () => FocusContent(), resolveOrigin: StateMenuOrigin);
        _groupMenuHost = new ContextMenuHost(_selGroup, ComposeGroupMenu, fallbackFocus: () => FocusContent(), resolveOrigin: GroupMenuOrigin);
        _selGroupText.Text = Texts.T("tagging.page.group.assign");
        _selGroup.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _selGroupText, CommandBindings.NamedIcon("chevron-down", 12) } };
        _selState.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _selStateText, CommandBindings.NamedIcon("chevron-down", 12) } };
        _selClear.Click += (_, _) => ClearSelection();
        _selClear.HorizontalAlignment = HorizontalAlignment.Right;
        _selClear.Classes.Add("command-icon");
        _selClear.Classes.Add(WorkspaceIcons.SquareClass);
        _selClear.Classes.Add("bar-button");

        Grid bar = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(_selectionActions, 0);
        bar.Children.Add(_selectionActions);
        StackPanel trailing = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        trailing.Children.Add(_selectionText);
        trailing.Children.Add(_selClear);
        Grid.SetColumn(trailing, 1);
        bar.Children.Add(trailing);
        _selectionBar.Child = bar;
        AutomationProperties.SetName(_selectionBar, Texts.T("tagging.page.selection.name"));
        return _selectionBar;
    }

    private void LocalizeSelection()
    {
        AutomationProperties.SetName(_selectionBar, Texts.T("tagging.page.selection.name"));
        string clear = Texts.T("media.selection.clear");
        string tip = clear + " · Esc";
        _selClear.Content = CommandBindings.NamedIcon("x");
        AutomationProperties.SetName(_selClear, clear);
        AutomationProperties.SetHelpText(_selClear, tip);
        ToolTip.SetTip(_selClear, tip);
        UpdateSelectionBar();
    }

    /// <summary>The number of selected entries (the bar's count).</summary>
    internal int SelectedCount => ActiveList.SelectedItems?.Count ?? 0;

    /// <summary>Clears the selection of the shown list or grid (Esc, the cross).</summary>
    internal void ClearSelection()
    {
        ActiveList.SelectedItems?.Clear();
        if (ActiveList.IsEffectivelyVisible && !ActiveList.IsKeyboardFocusWithin) ActiveList.Focus();
    }

    // Tag… / Tag together… anchored under the bar's own button (the popover opens where the person clicked).
    private System.Threading.Tasks.Task ExecuteSelectionAsync(string id, Control anchor) =>
        id is TaggingCommands.Tag or TaggingCommands.TagTogether
            ? CanRowCommand(id) ? TagSelectionAsync(anchor) : System.Threading.Tasks.Task.CompletedTask
            : ExecuteRowCommandAsync(id);

    private ContextMenuBuilder? ComposeStateMenu()
    {
        if (Lead == null) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("tagging.page.selection.moreState"));
        TaggingCommandContext context = CommandContext();
        foreach (string id in TaggingCommands.Sections[1])
        {
            string commandId = id;
            TaggingCommandState state = TaggingRowCommands.Evaluate(commandId, context);
            menu.Command(commandId, () => OpenActions.Start(() => ExecuteRowCommandAsync(commandId), "tagging.selection.state"), state.Available, state.Reason);
        }
        return menu;
    }

    /// <summary>
    /// The control the state menu opens on: its own button while that is on the screen (on the bar, or in the open More list), else the
    /// bar's More button (Barebones and Simple keep "Set state" under More; the host would open nothing on a hidden button), else the bar.
    /// </summary>
    // A button the toolbar moved into its closed More list is visible by its own flag but belongs to no visual tree (the popup is closed).
    private static bool OnScreen(Control control) => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;

    internal Control StateMenuOrigin() =>
        OnScreen(_selState) ? _selState
        : _selectionActions.More is { } more && OnScreen(more) ? more
        : _selectionBar;

    internal Control GroupMenuOrigin() =>
        OnScreen(_selGroup) ? _selGroup
        : _selectionActions.More is { } more && OnScreen(more) ? more
        : _selectionBar;

    /// <summary>The state menu of the bar as plain data (tests and smokes); null without a selection.</summary>
    internal ContextMenuModel? StateMenuModel() => ComposeStateMenu()?.ToModel();
    internal void ShowStateMenu() => _stateMenuHost?.Show(atPointer: false);
    internal ContextMenu? OpenStateMenu => _stateMenuHost?.OpenMenu;

    private void UpdateSelectionBar()
    {
        int count = SelectedCount;
        _selectionText.Text = count == 0 ? string.Empty : Texts.T("media.selection.count", count.ToString("N0", Texts.Culture));
        TaggingCommandContext context = CommandContext();
        string tagId = count > 1 ? TaggingCommands.TagTogether : TaggingCommands.Tag;
        SetSelectionAction(_selTag, tagId, CommandCatalog.Label(tagId), context);
        SetSelectionAction(_selStamp, TaggingCommands.ApplyStamp, CommandCatalog.Label(TaggingCommands.ApplyStamp), context);
        SetSelectionAction(_selDone, TaggingCommands.MarkDone, CommandCatalog.Label(TaggingCommands.MarkDone), context);
        SetSelectionAction(_selSkipped, TaggingCommands.MarkSkipped, CommandCatalog.Label(TaggingCommands.MarkSkipped), context);
        SetSelectionAction(_selContinue, TaggingCommands.ContinueHere, CommandCatalog.Label(TaggingCommands.ContinueHere), context);
        SetSelectionAction(_selRemove, TaggingCommands.Remove, Texts.T("tagging.page.selection.remove"), context);
        // The direct decisions stay visible; the menu remains the compact "More status" fallback for the complete state set.
        TaggingCommandState[] marks = TaggingCommands.Sections[1].Select(id => TaggingRowCommands.Evaluate(id, context)).ToArray();
        bool any = marks.Any(state => state.Available);
        string stateText = Texts.T("tagging.page.selection.moreState");
        _selStateText.Text = stateText;
        _selState.IsEnabled = any;
        string stateTip = any ? Texts.T("tagging.page.selection.state.hint") : marks[0].Reason ?? string.Empty;
        ToolTip.SetTip(_selState, stateTip.Length == 0 ? null : stateTip);
        ToolTip.SetShowOnDisabled(_selState, true);
        AutomationProperties.SetName(_selState, stateText);
        AutomationProperties.SetHelpText(_selState, stateTip);
        _selGroupText.Text = Texts.T("tagging.page.group.assign");
        // The menu starts with "New group…", so the button is useful before the first group exists (it used to stay disabled without a reason).
        _selGroup.IsEnabled = count > 0 && CanChange;
        AutomationProperties.SetName(_selGroup, Texts.T("tagging.page.group.assign"));
        WorkViewGate.Apply(_selectionBar, _workView, stateVisible: count > 0);
        _selClear.IsEnabled = count > 0;
        _selectionActions.RefreshOverflowLabel();
    }

    // One bar action: the visible text, a tip with the command's label and key (or the reason it is off) and the help text of the same.
    private static void SetSelectionAction(Button button, string id, string text, TaggingCommandContext context)
    {
        TaggingCommandState state = TaggingRowCommands.Evaluate(id, context);
        button.Content = text;
        button.IsEnabled = state.Available;
        string full = CommandCatalog.Label(id) + (CommandCatalog.ShortcutDisplay(id) is { } key ? " · " + key : string.Empty);
        string tip = state.Available ? full : state.Reason ?? full;
        ToolTip.SetTip(button, tip);
        ToolTip.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, tip);
    }
}
