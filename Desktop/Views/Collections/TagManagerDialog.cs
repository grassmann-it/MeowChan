using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

/// <summary>
/// Rename, merge and delete own tags. Deleting asks first, names how many threads and media versions lose
/// the tag, and keeps "Keep tag" as the focused default. Merging (two or more selected tags, Merge…) picks the
/// target, previews what changes and asks before it writes; it can be undone here as one step while nothing else
/// changed. A selected tag lists its other names (aliases), and probable duplicates are offered as suggestions only.
/// Every change is bound to the listed revision.
/// </summary>
internal sealed class TagManagerDialog : Window
{
    private readonly TagManagerSession _session;
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { Name = "TagManagerList", SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _selectHint = new() { Name = "TagManagerSelectHint", TextWrapping = TextWrapping.Wrap, Classes = { "workspace-help" } };
    private readonly ComboBox _sort = TagSortChoice.Create("TagManagerSort", TagListSort.Uses);
    private readonly TextBox _name = new() { Name = "TagManagerName" };
    private readonly Button _rename = new() { Name = "TagManagerRename" };
    private readonly Button _delete = new() { Name = "TagManagerDelete" };
    private readonly StackPanel _confirm = new() { Name = "TagManagerConfirm", Spacing = 8, IsVisible = false };
    private readonly TextBlock _confirmText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _confirmDelete = new() { Name = "TagManagerConfirmDelete" };
    private readonly Button _keep = new() { Name = "TagManagerKeep" };
    private readonly TextBlock _status = new() { Name = "TagManagerStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Name = "TagManagerError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _close = new() { Name = "TagManagerClose" };
    // Orphaned local media tag assignments: shown and removable explicitly, never retargeted.
    private readonly StackPanel _orphans = new() { Name = "TagManagerOrphans", Spacing = 8 };
    private readonly TextBlock _orphanText = new() { Name = "TagManagerOrphanText", TextWrapping = TextWrapping.Wrap };
    private readonly Button _orphanCheck = new() { Name = "TagManagerOrphanCheck" };
    private readonly Button _orphanRemove = new() { Name = "TagManagerOrphanRemove" };
    // Merge: the question with target, preview and the safe Cancel as default; Undo merge after it.
    private readonly Button _merge = new() { Name = "TagManagerMerge" };
    private readonly StackPanel _mergePanel = new() { Name = "TagManagerMergePanel", Spacing = 8, IsVisible = false };
    private readonly TextBlock _mergeTargetLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _mergeTarget = new() { Name = "TagManagerMergeTarget", MinWidth = 160 };
    private readonly TextBlock _mergePreview = new() { Name = "TagManagerMergePreview", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _mergeNote = new() { Name = "TagManagerMergeNote", TextWrapping = TextWrapping.Wrap, Classes = { "workspace-help" } };
    private readonly Button _mergeConfirm = new() { Name = "TagManagerMergeConfirm" };
    private readonly Button _mergeCancel = new() { Name = "TagManagerMergeCancel" };
    private readonly Button _undoMerge = new() { Name = "TagManagerUndoMerge", IsVisible = false };
    // Other names of the one selected tag.
    private readonly StackPanel _aliases = new() { Name = "TagManagerAliases", Spacing = 8 };
    private readonly TextBlock _aliasTitle = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _aliasList = new() { Name = "TagManagerAliasList", MaxHeight = 120 };
    private readonly TextBox _aliasName = new() { Name = "TagManagerAliasName" };
    private readonly Button _aliasAdd = new() { Name = "TagManagerAliasAdd" };
    private readonly Button _aliasRemove = new() { Name = "TagManagerAliasRemove" };
    // Probable duplicates: suggestions only.
    private readonly StackPanel _duplicates = new() { Name = "TagManagerDuplicates", Spacing = 8 };
    private readonly TextBlock _duplicateTitle = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _duplicateList = new() { Name = "TagManagerDuplicateList", MaxHeight = 160 };
    private readonly Button _duplicateMerge = new() { Name = "TagManagerDuplicateMerge" };
    private IReadOnlyList<TagId>? _merging;
    private TagId? _confirming;
    private bool _refreshing;
    private bool _closed;

    internal TagManagerSession Session => _session;
    internal ListBox List => _list;
    /// <summary>Most used first (the default) or A–Z.</summary>
    internal ComboBox SortChoice => _sort;
    internal TextBox NameInput => _name;
    internal Button RenameButton => _rename;
    internal Button DeleteButton => _delete;
    internal StackPanel ConfirmPanel => _confirm;
    internal TextBlock ConfirmText => _confirmText;
    internal Button ConfirmDeleteButton => _confirmDelete;
    internal Button KeepButton => _keep;
    internal TextBlock Status => _status;
    internal Button MergeButton => _merge;
    internal StackPanel MergePanel => _mergePanel;
    internal ComboBox MergeTarget => _mergeTarget;
    internal TextBlock MergePreview => _mergePreview;
    internal TextBlock MergeNote => _mergeNote;
    internal Button MergeConfirmButton => _mergeConfirm;
    internal Button MergeCancelButton => _mergeCancel;
    internal Button UndoMergeButton => _undoMerge;
    internal StackPanel AliasPanel => _aliases;
    internal ListBox AliasList => _aliasList;
    internal TextBox AliasName => _aliasName;
    internal Button AliasAddButton => _aliasAdd;
    internal Button AliasRemoveButton => _aliasRemove;
    internal StackPanel DuplicatePanel => _duplicates;
    internal ListBox DuplicateList => _duplicateList;
    internal Button DuplicateMergeButton => _duplicateMerge;
    internal TagManagerDialog(TagManagerSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Name = "TagManagerDialog";
        // Modals plan MD2: a modal form-step dialog; the title stands in the title bar only (the intro stays as the header).
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _list.ItemTemplate = new FuncDataTemplate<TagManagerRow>((row, _) => new TextBlock
        {
            Text = row == null ? "" : Texts.T("tags.manage.row", row.Name, row.Threads, row.Media),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4)
        });
        var edit = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        edit.Children.Add(_name); edit.Children.Add(_rename); Grid.SetColumn(_rename, 1);
        var confirmActions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        confirmActions.Children.Add(_keep); confirmActions.Children.Add(_confirmDelete);
        _confirm.Children.Add(_confirmText); _confirm.Children.Add(confirmActions);
        _mergeTarget.ItemTemplate = new FuncDataTemplate<TagManagerRow>((row, _) => new TextBlock { Text = row?.Name ?? string.Empty });
        var mergeTargetRow = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        mergeTargetRow.Children.Add(_mergeTargetLabel); mergeTargetRow.Children.Add(_mergeTarget);
        var mergeActions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        mergeActions.Children.Add(_mergeCancel); mergeActions.Children.Add(_mergeConfirm);
        _mergePanel.Children.Add(mergeTargetRow); _mergePanel.Children.Add(_mergePreview);
        _mergePanel.Children.Add(_mergeNote); _mergePanel.Children.Add(mergeActions);
        _aliasList.ItemTemplate = new FuncDataTemplate<PrivateTagAlias>((alias, _) => new TextBlock
        {
            Text = alias?.Name.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4)
        });
        var aliasEdit = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        aliasEdit.Children.Add(_aliasName); aliasEdit.Children.Add(_aliasAdd); Grid.SetColumn(_aliasAdd, 1);
        _aliasRemove.HorizontalAlignment = HorizontalAlignment.Left;
        _aliases.Children.Add(_aliasTitle); _aliases.Children.Add(_aliasList); _aliases.Children.Add(aliasEdit); _aliases.Children.Add(_aliasRemove);
        _duplicateList.ItemTemplate = new FuncDataTemplate<TagDuplicateGroup>((group, _) => new TextBlock
        {
            Text = group == null ? string.Empty : DuplicateText(group), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4)
        });
        _duplicateMerge.HorizontalAlignment = HorizontalAlignment.Left;
        _duplicates.Children.Add(_duplicateTitle); _duplicates.Children.Add(_duplicateList); _duplicates.Children.Add(_duplicateMerge);
        // UX103 P7 frame: intro on top, failures in the local strip, list and editing in the scrolling
        // body, outcome status and Close in the fixed footer.
        _list.MinHeight = 120; _list.MaxHeight = 280;
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 8) };
        _sort.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(_sort);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(_delete); actions.Children.Add(_merge); actions.Children.Add(_undoMerge);
        body.Children.Add(_list); body.Children.Add(_selectHint); body.Children.Add(edit); body.Children.Add(actions);
        body.Children.Add(_confirm); body.Children.Add(_mergePanel);
        body.Children.Add(_aliases); body.Children.Add(_duplicates);
        var orphanActions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        orphanActions.Children.Add(_orphanCheck); orphanActions.Children.Add(_orphanRemove);
        _orphans.Children.Add(_orphanText); _orphans.Children.Add(orphanActions);
        body.Children.Add(_orphans);
        var footer = DialogFooter.Build(_close, status: _status, statusAbove: true);
        DialogWindow.Frame(this, DialogFrame.Build(_intro, body, footer, DialogFrame.ErrorStrip(_error)), modal: true, captionName: "TagManagerChrome");

        _list.SelectionChanged += OnSelection;
        _sort.SelectionChanged += OnSortChanged;
        MeowChan.Desktop.Controls.IconButtons.WithText(_rename, "edit-2"); // I1b: Rename carries a catalog icon; Delete and its confirmation stay text (no trash icon in the catalog).
        _rename.Click += OnRename; _delete.Click += OnDelete; _confirmDelete.Click += OnConfirmDelete;
        _keep.Click += OnKeep; _close.Click += OnClose;
        _orphanCheck.Click += OnOrphanCheck; _orphanRemove.Click += OnOrphanRemove;
        _merge.Click += OnMerge; _mergeConfirm.Click += OnConfirmMerge; _mergeCancel.Click += OnCancelMerge; _undoMerge.Click += OnUndoMerge;
        _mergeTarget.SelectionChanged += OnMergeTargetChanged;
        _aliasAdd.Click += OnAliasAdd; _aliasRemove.Click += OnAliasRemove; _duplicateMerge.Click += OnDuplicateMerge;
        _aliasList.SelectionChanged += OnAliasSelection; _duplicateList.SelectionChanged += OnDuplicateSelection;
        _name.AddHandler(KeyDownEvent, OnNameKey, RoutingStrategies.Tunnel);
        _aliasName.AddHandler(KeyDownEvent, OnAliasKey, RoutingStrategies.Tunnel);
        // Escape first withdraws a pending delete or merge question (the safe answer), then closes.
        DialogEscape.Attach(this, () =>
        {
            if (_merging != null) { OnCancelMerge(this, new RoutedEventArgs()); return true; }
            if (_confirming == null) return false;
            OnKeep(this, new RoutedEventArgs()); return true;
        });
        Opened += OnOpened; Closed += OnClosed;
        _session.Updated += OnSessionUpdated; Texts.Changed += OnLanguageChanged;
        Refresh();
    }

    /// <summary>The one selected tag; null when none or several are selected (rename, delete and aliases need exactly one).</summary>
    private TagManagerRow? Selected => _list.SelectedItems is { Count: 1 } items ? items[0] as TagManagerRow : null;

    private IReadOnlyList<TagManagerRow> SelectedRows => _list.SelectedItems?.OfType<TagManagerRow>().ToArray() ?? Array.Empty<TagManagerRow>();

    private static string DuplicateText(TagDuplicateGroup group) => Texts.T(group.Reason == TagDuplicateReason.Spelling
        ? "tags.duplicates.spelling" : "tags.duplicates.similar", string.Join(", ", group.Tags.Select(tag => tag.Name.Display)));

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e) { if (!_refreshing) Refresh(); }

    private void OnOpened(object? sender, EventArgs e) { _list.Focus(); }
    private void OnSessionUpdated() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void OnSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        // A different tag: never carry a pending delete or merge question or an edited name over to it.
        _confirming = null;
        _merging = null;
        _session.ClearMessage();
        _name.Text = Selected?.Name ?? string.Empty;
        Refresh();
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await _session.RenameAsync(row.Id, _name.Text);
    }

    private async void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || !_rename.IsEffectivelyEnabled) return;
        e.Handled = true;
        if (Selected is { } row) await _session.RenameAsync(row.Id, _name.Text);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        _confirming = row.Id;
        _session.ClearMessage(); // An older outcome must not sit under a new question.
        Refresh();
        _keep.Focus(); // The safe answer is the default.
    }

    private async void OnConfirmDelete(object? sender, RoutedEventArgs e)
    {
        // Only the tag the question was asked about, and only while it is still the selected one.
        if (_confirming is not { } id || Selected?.Id != id) { _confirming = null; Refresh(); return; }
        _confirming = null;
        await _session.DeleteAsync(id);
    }

    private void OnKeep(object? sender, RoutedEventArgs e)
    {
        _confirming = null;
        Refresh();
        _list.Focus();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnMerge(object? sender, RoutedEventArgs e) => AskMerge(SelectedRows.Select(row => row.Id).ToArray());

    /// <summary>Opens the merge question for these tags (two or more), with the most used one as the proposed target.</summary>
    private void AskMerge(IReadOnlyList<TagId> tags)
    {
        if (!_session.CanMerge || tags.Count < 2) return;
        _confirming = null;
        _merging = tags;
        _session.ClearMessage();
        TagId? target = _session.DefaultMergeTarget(tags);
        _refreshing = true;
        try
        {
            TagManagerRow[] rows = _session.Rows.Where(row => tags.Contains(row.Id)).ToArray();
            _mergeTarget.ItemsSource = rows;
            _mergeTarget.SelectedItem = rows.FirstOrDefault(row => row.Id == target);
        }
        finally { _refreshing = false; }
        Refresh();
        _mergeCancel.Focus(); // The safe answer is the default.
    }

    private void OnMergeTargetChanged(object? sender, SelectionChangedEventArgs e) { if (!_refreshing) Refresh(); }

    private async void OnConfirmMerge(object? sender, RoutedEventArgs e)
    {
        // Only the tags the question was asked about, and only while they are still the selection.
        if (_merging is not { } tags || _mergeTarget.SelectedItem is not TagManagerRow target
            || !SelectedRows.Select(row => row.Id).ToHashSet().SetEquals(tags)) { _merging = null; Refresh(); return; }
        _merging = null;
        await _session.MergeAsync(tags, target.Id);
        if (_closed) return;
        // The target stays selected so its new other names show at once.
        _refreshing = true;
        try { _list.SelectedItems?.Clear(); if (_session.Rows.FirstOrDefault(row => row.Id == target.Id) is { } row) _list.SelectedItems?.Add(row); }
        finally { _refreshing = false; }
        Refresh();
        if (_undoMerge.IsVisible) _undoMerge.Focus(); else _list.Focus();
    }

    private void OnCancelMerge(object? sender, RoutedEventArgs e)
    {
        _merging = null;
        Refresh();
        _list.Focus();
    }

    private async void OnUndoMerge(object? sender, RoutedEventArgs e)
    {
        await _session.UndoMergeAsync();
        if (!_closed) _list.Focus();
    }

    private async void OnAliasAdd(object? sender, RoutedEventArgs e) => await AddAliasAsync();

    private async void OnAliasKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || !_aliasAdd.IsEffectivelyEnabled) return;
        e.Handled = true;
        await AddAliasAsync();
    }

    private async System.Threading.Tasks.Task AddAliasAsync()
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(_aliasName.Text)) return;
        string text = _aliasName.Text;
        await _session.AddAliasAsync(row.Id, text);
        if (!_session.MessageIsError && string.Equals(_aliasName.Text, text, StringComparison.Ordinal)) _aliasName.Text = string.Empty;
    }

    private async void OnAliasRemove(object? sender, RoutedEventArgs e)
    {
        if (_aliasList.SelectedItem is PrivateTagAlias alias) await _session.RemoveAliasAsync(alias.Name.Display);
    }

    private void OnAliasSelection(object? sender, SelectionChangedEventArgs e) { if (!_refreshing) Refresh(); }
    private void OnDuplicateSelection(object? sender, SelectionChangedEventArgs e) { if (!_refreshing) Refresh(); }

    /// <summary>Selects the tags of the chosen suggestion in the list and asks the merge question for them; nothing is merged yet.</summary>
    private void OnDuplicateMerge(object? sender, RoutedEventArgs e)
    {
        if (_duplicateList.SelectedItem is not TagDuplicateGroup group) return;
        HashSet<TagId> ids = group.Tags.Select(tag => tag.Id).ToHashSet();
        _refreshing = true;
        try
        {
            _list.SelectedItems?.Clear();
            foreach (TagManagerRow row in _list.ItemsSource?.OfType<TagManagerRow>() ?? Enumerable.Empty<TagManagerRow>())
                if (ids.Contains(row.Id)) _list.SelectedItems?.Add(row);
            _name.Text = string.Empty;
        }
        finally { _refreshing = false; }
        AskMerge(group.Tags.Select(tag => tag.Id).ToArray());
    }

    /// <summary>The preview sentence and the note of the open merge question, or null when no valid merge is open.</summary>
    internal (string Preview, string Note)? MergeTexts()
    {
        if (_merging is not { } tags || _mergeTarget.SelectedItem is not TagManagerRow target
            || _session.Preview(tags, target.Id) is not { } preview) return null;
        return (Texts.T("tags.merge.preview", preview.Media, preview.Threads, preview.Target.Name, preview.MergedNames),
            Texts.T("tags.merge.note", preview.Target.Name));
    }

    private async void OnOrphanCheck(object? sender, RoutedEventArgs e) => await _session.CheckOrphansAsync();

    private async void OnOrphanRemove(object? sender, RoutedEventArgs e) => await _session.RemoveOrphansAsync();

    /// <summary>Called when the owning page ends; never leaves a modal behind.</summary>
    internal void CloseForLifetime() { if (!_closed) Close(); }

    private void Refresh()
    {
        if (_closed) return;
        Title = Texts.T("tags.manage.title");
        _intro.Text = Texts.T("tags.manage.intro");
        AutomationProperties.SetName(_list, Texts.T("tags.manage.list"));
        _name.PlaceholderText = Texts.T("tags.manage.name");
        AutomationProperties.SetName(_name, Texts.T("tags.manage.name"));
        void Label(Button button, string key) { button.Content = Texts.T(key); AutomationProperties.SetName(button, Texts.T(key)); }
        Label(_rename, "tags.manage.rename"); Label(_delete, "tags.manage.delete");
        Label(_confirmDelete, "tags.manage.confirmDelete"); Label(_keep, "tags.manage.keep"); _close.Content = Texts.T("action.close"); AutomationProperties.SetName(_close, Texts.T("action.close"));
        Label(_orphanCheck, "tags.manage.orphansCheck"); Label(_orphanRemove, "tags.manage.orphansRemove");
        Label(_merge, "tags.merge.open"); Label(_mergeConfirm, "tags.merge.confirm"); Label(_mergeCancel, "action.cancel");
        Label(_undoMerge, "tags.merge.undo"); Label(_aliasAdd, "tags.alias.add"); Label(_aliasRemove, "tags.alias.remove");
        Label(_duplicateMerge, "tags.duplicates.merge");
        _selectHint.Text = Texts.T("tags.merge.selectHint");
        _mergeTargetLabel.Text = Texts.T("tags.merge.target");
        AutomationProperties.SetName(_mergeTarget, Texts.T("tags.merge.target"));
        _aliasName.PlaceholderText = Texts.T("tags.alias.name");
        AutomationProperties.SetName(_aliasName, Texts.T("tags.alias.name"));
        AutomationProperties.SetName(_aliasList, Texts.T("tags.alias.list"));
        _duplicateTitle.Text = Texts.T("tags.duplicates.title");
        AutomationProperties.SetName(_duplicateList, Texts.T("tags.duplicates.title"));

        HashSet<TagId> selected = SelectedRows.Select(row => row.Id).ToHashSet();
        string? alias = (_aliasList.SelectedItem as PrivateTagAlias)?.Name.Key;
        TagDuplicateGroup? duplicate = _duplicateList.SelectedItem as TagDuplicateGroup;
        _refreshing = true;
        try
        {
            TagSortChoice.Localize(_sort);
            IReadOnlyList<TagManagerRow> rows = TagManagerRow.Sorted(_session.Rows, TagSortChoice.Of(_sort));
            _list.ItemsSource = rows;
            _list.SelectedItems?.Clear();
            foreach (TagManagerRow row in rows) if (selected.Contains(row.Id)) _list.SelectedItems?.Add(row);
            IReadOnlyList<PrivateTagAlias> aliases = Selected is { } one ? _session.AliasesOf(one.Id) : Array.Empty<PrivateTagAlias>();
            _aliasList.ItemsSource = aliases;
            _aliasList.SelectedItem = aliases.FirstOrDefault(item => item.Name.Key == alias);
            IReadOnlyList<TagDuplicateGroup> groups = _session.CanMerge ? _session.Duplicates() : Array.Empty<TagDuplicateGroup>();
            _duplicateList.ItemsSource = groups;
            HashSet<TagId>? previous = duplicate?.Tags.Select(tag => tag.Id).ToHashSet();
            _duplicateList.SelectedItem = previous == null ? null : groups.FirstOrDefault(group => previous.SetEquals(group.Tags.Select(tag => tag.Id)));
        }
        finally { _refreshing = false; }
        TagManagerRow? current = Selected;
        // The selected tag vanished (deleted here or elsewhere): drop its question and its draft name.
        if (current == null) { _confirming = null; if (selected.Count == 1) _name.Text = string.Empty; }
        else if (_confirming != null && _confirming != current.Id) _confirming = null;
        // A merge question only stands for exactly the tags it was asked about, all still there.
        if (_merging != null && !selected.SetEquals(_merging)) _merging = null;

        bool ready = _session.Snapshot != null && !_session.Busy;
        bool asking = _confirming != null || _merging != null;
        _name.IsEnabled = ready && current != null;
        _rename.IsEnabled = ready && current != null;
        _delete.IsEnabled = ready && current != null && !asking;
        _confirm.IsVisible = _confirming != null && current != null;
        if (_confirm.IsVisible)
            _confirmText.Text = Texts.T("tags.manage.confirm", current!.Name, current.Threads, current.Media);
        _confirmDelete.IsEnabled = _keep.IsEnabled = ready;
        // Merging: two or more selected tags, then the question with target, preview and note.
        _merge.IsVisible = _session.CanMerge;
        _selectHint.IsVisible = _session.CanMerge;
        _merge.IsEnabled = ready && selected.Count >= 2 && !asking;
        (string Preview, string Note)? merge = MergeTexts();
        _mergePanel.IsVisible = merge != null;
        if (merge is { } texts) { _mergePreview.Text = texts.Preview; _mergeNote.Text = texts.Note; }
        _mergeConfirm.IsEnabled = ready && merge != null;
        _mergeCancel.IsEnabled = ready;
        _undoMerge.IsVisible = _session.CanUndoMerge;
        _undoMerge.IsEnabled = ready && !asking;
        _aliases.IsVisible = _session.CanMerge && current != null;
        if (current != null)
            _aliasTitle.Text = Texts.T(_aliasList.ItemCount == 0 ? "tags.alias.titleNone" : "tags.alias.title", current.Name);
        _aliasList.IsVisible = _aliasList.ItemCount > 0;
        _aliasName.IsEnabled = ready && current != null;
        _aliasAdd.IsEnabled = ready && current != null;
        _aliasRemove.IsVisible = _aliasList.ItemCount > 0;
        _aliasRemove.IsEnabled = ready && _aliasList.SelectedItem != null;
        _duplicates.IsVisible = _session.CanMerge && _duplicateList.ItemCount > 0;
        _duplicateMerge.IsEnabled = ready && _duplicateList.SelectedItem != null && !asking;
        LocalMediaTagInspection? inspection = _session.Inspection;
        // Only profiles that tag loose files see the orphan check; everyone else keeps the plain manager.
        _orphans.IsVisible = _session.CanInspectOrphans
            && (_session.Snapshot?.LocalAssignments.Count > 0 || inspection != null || _session.Inspecting);
        _orphanText.Text = _session.Inspecting ? Texts.T("tags.manage.orphansChecking")
            : inspection == null ? Texts.T("tags.manage.orphansIntro")
            : string.Join(" ", new[]
            {
                inspection.Orphans.Count > 0 ? Texts.T("tags.manage.orphans", inspection.Orphans.Count, inspection.OrphanedFiles) : null,
                inspection.UncheckedFiles > 0 ? Texts.Count("tags.manage.orphansUnchecked", inspection.UncheckedFiles) : null
            }.OfType<string>());
        if (_orphanText.Text.Length == 0) _orphanText.Text = Texts.T("tags.manage.orphansNone");
        _orphanCheck.IsEnabled = ready && !_session.Inspecting && _confirming == null;
        _orphanRemove.IsVisible = inspection is { Orphans.Count: > 0 };
        _orphanRemove.IsEnabled = ready && !_session.Inspecting && _confirming == null;

        _status.Text = _session.Snapshot == null ? Texts.T("tags.unavailable")
            : _session.MessageKey == "tags.merge.done" ? Texts.T("tags.merge.done", _session.LastMergeTarget ?? string.Empty)
            : _session.MessageKey != null ? Texts.T(_session.MessageKey)
            : _session.Rows.Count == 0 ? Texts.T("tags.none") : string.Empty;
        // Status keeps the outcome text for its readers; failures are shown in the local error strip.
        bool error = _session.Snapshot == null || _session.MessageKey != null && _session.MessageIsError;
        _error.Text = error ? _status.Text : string.Empty;
        _status.IsVisible = !error && _status.Text.Length > 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _session.Updated -= OnSessionUpdated; Texts.Changed -= OnLanguageChanged;
        _list.SelectionChanged -= OnSelection;
        _sort.SelectionChanged -= OnSortChanged;
        _rename.Click -= OnRename; _delete.Click -= OnDelete; _confirmDelete.Click -= OnConfirmDelete;
        _keep.Click -= OnKeep; _close.Click -= OnClose;
        _orphanCheck.Click -= OnOrphanCheck; _orphanRemove.Click -= OnOrphanRemove;
        _merge.Click -= OnMerge; _mergeConfirm.Click -= OnConfirmMerge; _mergeCancel.Click -= OnCancelMerge; _undoMerge.Click -= OnUndoMerge;
        _mergeTarget.SelectionChanged -= OnMergeTargetChanged;
        _aliasAdd.Click -= OnAliasAdd; _aliasRemove.Click -= OnAliasRemove; _duplicateMerge.Click -= OnDuplicateMerge;
        _aliasList.SelectionChanged -= OnAliasSelection; _duplicateList.SelectionChanged -= OnDuplicateSelection;
        _aliasName.RemoveHandler(KeyDownEvent, OnAliasKey);
        _name.RemoveHandler(KeyDownEvent, OnNameKey);
        Opened -= OnOpened; Closed -= OnClosed;
    }
}
