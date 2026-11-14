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
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;

namespace MeowChan;

/// <summary>TG2's named queue scopes: the page keeps the existing state-filter seam and narrows it with this selection.</summary>
public sealed partial class TaggingPage
{
    private readonly StackPanel _groupPanel = new() { Name = "TaggingGroupPanel", Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _groupLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _group = new() { Name = "TaggingGroup", MinWidth = 170 };
    private readonly Button _groupNew = new() { Name = "TaggingGroupNew" };
    private readonly Button _groupRename = new() { Name = "TaggingGroupRename" };
    private readonly Button _groupDelete = new() { Name = "TaggingGroupDelete" };
    private string? _selectedGroupId;
    private bool _ungroupedSelected;
    private bool _groupLocalizing;

    internal ComboBox GroupBox => _group;
    internal string? SelectedGroupId => _selectedGroupId;
    internal bool UngroupedGroupSelected => _ungroupedSelected;
    internal IReadOnlyList<TaggingGroupChoice> GroupChoices => TaggingQueueView.GroupChoices(_queueWork.Queue);
    internal Button NewGroupButton => _groupNew;
    internal Button RenameGroupButton => _groupRename;
    internal Button DeleteGroupButton => _groupDelete;

    private bool InSelectedGroup(TagQueueItem item) => TaggingQueueView.InGroup(item, _selectedGroupId, _ungroupedSelected);
    private Func<TagQueueItem, bool>? EffectiveScope => Scope == null && _selectedGroupId == null && !_ungroupedSelected
        ? null
        : item => InSelectedGroup(item) && (Scope == null || Scope(item));

    private void InitializeGroups()
    {
        WorkViewElement.SetId(_groupPanel, "tagging.group");
        _group.ItemTemplate = new FuncDataTemplate<TaggingGroupChoice>((choice, _) => new TextBlock { Text = choice.Label, TextTrimming = TextTrimming.CharacterEllipsis });
        _group.SelectionChanged += (_, _) =>
        {
            if (_groupLocalizing || _group.SelectedItem is not TaggingGroupChoice choice) return;
            _selectedGroupId = choice.Id;
            _ungroupedSelected = choice.Ungrouped;
            _queueWork.SelectGroup(_selectedGroupId, _ungroupedSelected);
            Render();
            RefreshWorkView();
        };
        _groupNew.Click += async (_, _) => await CreateGroupDialogAsync();
        _groupRename.Click += async (_, _) => await RenameGroupDialogAsync();
        _groupDelete.Click += async (_, _) => await DeleteGroupAsync();
        _groupPanel.Children.Add(_groupLabel);
        _groupPanel.Children.Add(_group);
        _groupPanel.Children.Add(_groupNew);
        _groupPanel.Children.Add(_groupRename);
        _groupPanel.Children.Add(_groupDelete);
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
        _groupRename.IsEnabled = _selectedGroupId != null && CanChange;
        _groupDelete.IsEnabled = _selectedGroupId != null && CanChange;
        _groupNew.IsEnabled = CanChange;
        AutomationProperties.SetName(_group, _groupLabel.Text ?? Texts.T("tagging.page.group"));
    }

    private void LocalizeGroups()
    {
        _groupLabel.Text = Texts.T("tagging.page.group");
        _groupNew.Content = "+";
        _groupRename.Content = Texts.T("tagging.page.group.rename");
        _groupDelete.Content = Texts.T("tagging.page.group.delete");
        AutomationProperties.SetName(_groupNew, Texts.T("tagging.page.group.new"));
        AutomationProperties.SetName(_groupRename, Texts.T("tagging.page.group.rename"));
        AutomationProperties.SetName(_groupDelete, Texts.T("tagging.page.group.delete"));
        AutomationProperties.SetName(_group, Texts.T("tagging.page.group"));
        RenderGroups();
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

    internal ContextMenuModel? GroupMenuModel() => ComposeGroupMenu()?.ToModel();

    private ContextMenuBuilder? ComposeGroupMenu()
    {
        if (SelectedCount == 0) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("tagging.page.group.assign"));
        menu.Item(TaggingCommands.GroupNew, Texts.T("tagging.page.group.new"), () => _ = CreateGroupDialogAsync(), CanChange);
        foreach (TagQueueGroup group in _queueWork.Queue.Groups)
        {
            TagQueueGroup target = group;
            bool checkedValue = Selected.All(row => row.Item.GroupId == target.Id);
            menu.Radio("tagging-groups", TaggingCommands.GroupPrefix + target.Id, target.Name, checkedValue,
                () => _ = AssignSelectionToGroupAsync(target.Id));
        }
        menu.Item(TaggingCommands.GroupRemove, Texts.T("tagging.page.group.remove"), () => _ = AssignSelectionToGroupAsync(null),
            CanChange && Selected.Any(row => row.Item.GroupId != null));
        return menu;
    }

    private Task AssignSelectionToGroupAsync(string? groupId) => AfterAsync(_queueWork.AssignGroupAsync(Selected.Select(row => row.Key).ToArray(), groupId));

    private sealed class GroupNameDialog
    {
        internal static async Task<string?> ShowAsync(Control owner, string title, string initial)
        {
            if (TopLevel.GetTopLevel(owner) is not Window windowOwner) return null;
            Window dialog = DialogWindow.Create(title, DialogSize.S, modal: true, heightToContent: true);
            TextBox input = new() { Text = initial, MinWidth = 300, PlaceholderText = Texts.T("tagging.page.group.name") };
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
