using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;

namespace MeowChan;

/// <summary>TG2's named queue scopes: the page keeps the existing state-filter seam and narrows it with this selection.</summary>
public sealed partial class TaggingPage
{
    private readonly ComboBox _group = new() { Name = "TaggingGroup", Width = 160 };
    private readonly Button _groupManage = new() { Name = "TaggingGroupManage" };
    private ContextMenuHost? _groupManageHost;
    private string? _selectedGroupId;
    private bool _ungroupedSelected;
    private bool _groupLocalizing;

    internal ComboBox GroupBox => _group;
    internal string? SelectedGroupId => _selectedGroupId;
    internal bool UngroupedGroupSelected => _ungroupedSelected;
    internal IReadOnlyList<TaggingGroupChoice> GroupChoices => TaggingQueueView.GroupChoices(_queueWork.Queue);
    internal Button GroupManageButton => _groupManage;

    private Func<TagQueueItem, bool>? _effectiveScope;

    // Freeze membership once per render so the marker, commands and asynchronous gallery handoff share one scope.
    private void CaptureScope(TagQueueSnapshot queue)
    {
        bool untagged = Filter == TaggingQueueFilter.Untagged;
        if (!untagged && Scope == null && _selectedGroupId == null && !_ungroupedSelected)
        {
            _effectiveScope = null;
            return;
        }
        var keys = queue.Items.Where(item => TaggingQueueView.InGroup(item, _selectedGroupId, _ungroupedSelected)
            && (Scope == null || Scope(item))
            && (!untagged || (_userData?.TagSnapshot != null && _userData.LacksMediaTags(EntryOf(item)))))
            .Select(item => item.Key).ToHashSet();
        _effectiveScope = item => keys.Contains(item.Key);
    }

    // The combo box builds its selection box while ItemsSource is being swapped, and then the item is null for a moment:
    // the template must tolerate that (an exception here aborted Render and left the queue table empty).
    internal static FuncDataTemplate<TaggingGroupChoice> GroupChoiceTemplate() =>
        new((choice, _) => new TrimTipTextBlock { Text = choice?.Label });

    private void InitializeGroups()
    {
        _group.ItemTemplate = GroupChoiceTemplate();
        _group.SelectionChanged += (_, _) =>
        {
            if (_groupLocalizing || _group.SelectedItem is not TaggingGroupChoice choice) return;
            _selectedGroupId = choice.Id;
            _ungroupedSelected = choice.Ungrouped;
            _queueWork.SelectGroup(_selectedGroupId, _ungroupedSelected);
            Render();
        };
        _groupManageHost = new ContextMenuHost(_groupManage, ComposeGroupManageMenu, fallbackFocus: () => FocusContent(),
            resolveOrigin: () => _groupManage.IsAttachedToVisualTree() ? _groupManage : _actions.More ?? (Control)_groupManage);
        _groupManage.Click += (_, _) => _groupManageHost?.Show(atPointer: false);
        RenderGroups();
    }

    private void RenderGroups()
    {
        IReadOnlyList<TaggingGroupChoice> choices = GroupChoices;
        TaggingGroupChoice? selected = choices.FirstOrDefault(choice => choice.Id == _selectedGroupId && choice.Ungrouped == _ungroupedSelected)
            ?? choices[0];
        _selectedGroupId = selected.Id;
        _ungroupedSelected = selected.Ungrouped;
        _queueWork.SelectGroup(_selectedGroupId, _ungroupedSelected);
        _groupLocalizing = true;
        try
        {
            _group.ItemsSource = choices;
            _group.SelectedItem = selected;
        }
        finally { _groupLocalizing = false; }
        // The button stays usable while "New group" is: the entries inside the menu carry their own enabled rules.
        _groupManage.IsEnabled = CanChange;
        ToolTip.SetTip(_group, selected.Label);
        AutomationProperties.SetHelpText(_group, selected.Label);
    }

    private void LocalizeGroups()
    {
        string manage = Texts.T("tagging.page.group.manage");
        _groupManage.Content = manage;
        ToolTip.SetTip(_groupManage, manage);
        AutomationProperties.SetName(_groupManage, manage);
        AutomationProperties.SetName(_group, Texts.T("tagging.page.group"));
        RenderGroups();
    }

    /// <summary>
    /// Runs a group menu action whose task nobody awaits (a menu callback is synchronous). A failure does not vanish in the dropped task:
    /// it is said in the page's status line, like the failure of any other queue change.
    /// </summary>
    private async Task RunGroupAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_queueWork.Ended) return;
            ShowStatus(TagQueueActions.DescribeFailure(error));
        }
    }

    private async Task CreateGroupDialogAsync()
    {
        IReadOnlyList<TaggingQueueRow> selected = Selected;
        string? name = await GroupNameDialog.ShowAsync(this, Texts.T("tagging.page.group.new"), string.Empty);
        if (name == null) return;
        bool done = await _queueWork.CreateGroupAsync(name, selected.Select(row => row.Key).ToArray());
        if (done) _selectedGroupId = _queueWork.Queue.Groups[^1].Id;
        Show();
        ShowStatus(_queueWork.Status);
    }

    private async Task RenameGroupDialogAsync()
    {
        if (_selectedGroupId == null || _queueWork.Queue.Group(_selectedGroupId) is not { } group) return;
        string? name = await GroupNameDialog.ShowAsync(this, Texts.T("tagging.page.group.rename"), group.Name);
        if (name == null) return;
        bool done = await _queueWork.RenameGroupAsync(group.Id, name);
        Show();
        ShowStatus(_queueWork.Status);
        if (done) RenderGroups();
    }

    private async Task DeleteGroupAsync()
    {
        if (_selectedGroupId == null || TopLevel.GetTopLevel(this) is not Window owner || _queueWork.Queue.Group(_selectedGroupId) is not { } group) return;
        bool confirmed = await DialogPrompt.ConfirmAsync(owner, Texts.T("tagging.page.group.delete"),
            Texts.T("tagging.page.group.delete.ask", group.Name), Texts.T("tagging.page.group.delete"), Texts.T("queue.ask.no"));
        if (!confirmed) return;
        bool done = await _queueWork.DeleteGroupAsync(group.Id);
        if (done) { _selectedGroupId = null; _ungroupedSelected = true; }
        Show();
        ShowStatus(_queueWork.Status);
    }

    /// <summary>The "Manage groups" menu as plain data (tests and smokes).</summary>
    internal ContextMenuModel? GroupManageMenuModel() => ComposeGroupManageMenu().ToModel();

    // New group, Rename and Delete as the three buttons were: the same handlers and the same enabled rules (RenameGroup and DeleteGroup
    // need a named group to be selected).
    private ContextMenuBuilder ComposeGroupManageMenu()
    {
        bool named = _selectedGroupId != null && CanChange;
        return new ContextMenuBuilder().Named(Texts.T("tagging.page.group.manage"))
            .Item("tagging.group.new", Texts.T("tagging.page.group.newMenu"), () => _ = RunGroupAction(CreateGroupDialogAsync), CanChange)
            .Item("tagging.group.rename", Texts.T("tagging.page.group.rename"), () => _ = RunGroupAction(RenameGroupDialogAsync), named)
            .Item("tagging.group.delete", Texts.T("tagging.page.group.delete"), () => _ = RunGroupAction(DeleteGroupAsync), named);
    }

    private Task AssignSelectionToGroupAsync(string? groupId) => AfterAsync(_queueWork.AssignGroupAsync(Selected.Select(row => row.Key).ToArray(), groupId));

    private sealed class GroupNameDialog
    {
        internal static async Task<string?> ShowAsync(Control owner, string title, string initial)
        {
            if (TopLevel.GetTopLevel(owner) is not Window windowOwner) return null;
            Window dialog = DialogWindow.Create(title, DialogSize.S, modal: true, heightToContent: true);
            TextBox input = new() { Text = initial, MinWidth = 300, PlaceholderText = Texts.T("tagging.page.group.name") };
            AutomationProperties.SetName(input, Texts.T("tagging.page.group.name"));
            Button cancel = new() { Content = Texts.T("queue.ask.no") };
            Button accept = new() { Content = Texts.T("queue.ask.yes"), Classes = { "primary" } };
            accept.Click += (_, _) => dialog.Close(input.Text.Trim());
            cancel.Click += (_, _) => dialog.Close(null);
            input.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; dialog.Close(input.Text.Trim()); } };
            DialogWindow.Frame(dialog, DialogFrame.Build(null, new Border { Child = input, Padding = new Thickness(16, 0, 16, 16) },
                DialogFooter.Build(accept, new Control[] { cancel })), modal: true);
            DialogEscape.Attach(dialog, close: () => dialog.Close(null));
            dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
            string? result = await dialog.ShowDialog<string?>(windowOwner);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
    }
}
