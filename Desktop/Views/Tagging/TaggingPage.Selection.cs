using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;

namespace MeowChan;

public sealed partial class TaggingPage
{
    private readonly Border _selectionBar = new() { Name = "TaggingSelectionBar", Classes = { "tagging-selection" }, IsVisible = false,
        Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _selectionText = new() { Name = "TaggingSelectionStatus", Classes = { "tnum" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _selTag = new TaggingActivationButton() { Name = "TaggingSelectionTag", Classes = { "bar-button" } };
    private readonly Button _selDone = new TaggingActivationButton() { Name = "TaggingSelectionDone", Classes = { "bar-button" } };
    private readonly Button _selMore = new TaggingActivationButton() { Name = "TaggingSelectionMore", Classes = { "bar-button" } };
    private readonly TaggingControlRow _selectionRow = new() { Name = "TaggingSelectionRow" };
    private readonly Button _selClear = new TaggingActivationButton() { Name = "TaggingSelectionClear", Classes = { "bar-button" } };
    private ContextMenuHost? _selectionMenuHost;

    internal Border SelectionBar => _selectionBar;
    internal TextBlock SelectionText => _selectionText;
    internal Button SelectionTagButton => _selTag;
    internal Button SelectionDoneButton => _selDone;
    internal Button SelectionMoreButton => _selMore;
    internal Button SelectionClearButton => _selClear;

    private Control BuildSelectionBar()
    {
        AutomationProperties.SetLiveSetting(_selectionText, AutomationLiveSetting.Polite);
        _selTag.Click += (_, _) => OpenActions.Start(() => CanRowCommand(SelectionTagCommand)
            ? TagSelectionAsync(_selTag) : System.Threading.Tasks.Task.CompletedTask, "tagging.selection.tag");
        _selDone.Click += (_, _) => OpenActions.Start(() => ExecuteRowCommandAsync(TaggingCommands.MarkDone), "tagging.selection.done");
        _selectionMenuHost = new ContextMenuHost(_selMore, ComposeSelectionMenu, fallbackFocus: () => FocusContent());
        _selMore.Click += (_, _) => ShowSelectionMenu();
        CommandBindings.ShowIconBeforeText(_selMore, CommandCatalog.Get("menu.context-open").Icon,
            showLabel: _selectionRow.GetObservable(TaggingControlRow.ShowActionLabelsProperty));
        _selClear.Click += (_, _) => ClearSelection();
        _selectionBar.KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || OpenSelectionMenu != null) return;
            e.Handled = true;
            ClearSelection();
        };

        StackPanel actions = new() { Name = "TaggingSelectionActions", Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _selTag, _selDone, _selMore } };
        _selectionRow.Children.AddRange([_selectionText, actions, _selClear]);
        _selectionRow.CompactActions = actions;
        _selectionRow.TrailingChild = _selClear;
        _selectionBar.Child = _selectionRow;
        CommandBindings.ObservePresentation(_selectionBar, UpdateSelectionBar);
        return _selectionBar;
    }

    private void LocalizeSelection()
    {
        AutomationProperties.SetName(_selectionBar, Texts.T("tagging.page.selection.name"));
        string more = Texts.T("workview.more");
        _selMore.Content = more;
        ToolTip.SetTip(_selMore, more);
        AutomationProperties.SetName(_selMore, more);
        AutomationProperties.SetHelpText(_selMore, Texts.T("tagging.page.selection.name"));
        string clear = Texts.T("media.selection.clear");
        WorkspaceIcons.MakeSquare(_selClear, WorkspaceIcons.CloseSmall, clear);
        string tip = clear + " · Esc";
        AutomationProperties.SetHelpText(_selClear, tip);
        ToolTip.SetTip(_selClear, tip);
        UpdateSelectionBar();
    }

    internal int SelectedCount => ActiveList.SelectedItems?.Count ?? 0;

    internal void ClearSelection()
    {
        TagQueueKey? focus = FocusedResultKey() ?? Lead?.Key;
        ActiveList.SelectedItems?.Clear();
        FocusContent(focus);
    }

    private string SelectionTagCommand => SelectedCount > 1 ? TaggingCommands.TagTogether : TaggingCommands.Tag;

    private ContextMenuBuilder? ComposeSelectionMenu() => SelectedCount == 0 ? null
        : TaggingQueueMenu.ComposeSelection(CommandContext(), id =>
            () => OpenActions.Start(() => ExecuteRowCommandAsync(id), "tagging.selection.more"));

    internal ContextMenuModel? SelectionMenuModel() => ComposeSelectionMenu()?.ToModel();
    internal void ShowSelectionMenu() => _selectionMenuHost?.Show(atPointer: false);
    internal ContextMenu? OpenSelectionMenu => _selectionMenuHost?.OpenMenu;

    private void UpdateSelectionBar()
    {
        int count = SelectedCount;
        _selectionText.Text = count == 0 ? string.Empty : Texts.T("media.selection.count", count.ToString("N0", Texts.Culture));
        TaggingCommandContext context = CommandContext();
        SetSelectionAction(_selTag, SelectionTagCommand, context);
        SetSelectionAction(_selDone, TaggingCommands.MarkDone, context);
        if (count == 0) _selectionMenuHost?.Close();
        WorkViewGate.Apply(_selectionBar, _workView, stateVisible: count > 0);
        _selClear.IsEnabled = count > 0;
    }

    private void SetSelectionAction(Button button, string id, TaggingCommandContext context)
    {
        TaggingCommandState state = TaggingRowCommands.Evaluate(id, context);
        string label = CommandCatalog.Label(id);
        string tip = CommandBindings.Tooltip(id, state.Reason, keyContext: "tagging");
        button.Content = label;
        string icon = CommandCatalog.Get(id).Icon;
        if (IconButtons.KeyOf(button) != icon)
            CommandBindings.ShowIconBeforeText(button, icon, showLabel: _selectionRow.GetObservable(TaggingControlRow.ShowActionLabelsProperty));
        button.IsEnabled = state.Available;
        ToolTip.SetTip(button, tip);
        ToolTip.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, tip);
    }
}
