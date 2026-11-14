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
/// Rename and delete own tags. Deleting asks first, names how many threads and media versions lose
/// the tag, and keeps "Keep tag" as the focused default. Every change is bound to the listed revision.
/// </summary>
internal sealed class TagManagerDialog : Window
{
    private readonly TagManagerSession _session;
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { Name = "TagManagerList" };
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
        // UX103 P7 frame: intro on top, failures in the local strip, list and editing in the scrolling
        // body, outcome status and Close in the fixed footer.
        _list.MinHeight = 120; _list.MaxHeight = 280;
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 8) };
        _sort.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(_sort);
        body.Children.Add(_list); body.Children.Add(edit); body.Children.Add(_delete); body.Children.Add(_confirm);
        var orphanActions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        orphanActions.Children.Add(_orphanCheck); orphanActions.Children.Add(_orphanRemove);
        _orphans.Children.Add(_orphanText); _orphans.Children.Add(orphanActions);
        body.Children.Add(_orphans);
        _delete.HorizontalAlignment = HorizontalAlignment.Left;
        var footer = DialogFooter.Build(_close, status: _status, statusAbove: true);
        DialogWindow.Frame(this, DialogFrame.Build(_intro, body, footer, DialogFrame.ErrorStrip(_error)), modal: true, captionName: "TagManagerChrome");

        _list.SelectionChanged += OnSelection;
        _sort.SelectionChanged += OnSortChanged;
        _rename.Click += OnRename; _delete.Click += OnDelete; _confirmDelete.Click += OnConfirmDelete;
        _keep.Click += OnKeep; _close.Click += OnClose;
        _orphanCheck.Click += OnOrphanCheck; _orphanRemove.Click += OnOrphanRemove;
        _name.AddHandler(KeyDownEvent, OnNameKey, RoutingStrategies.Tunnel);
        // Escape first withdraws a pending delete question (Keep tag), then closes.
        DialogEscape.Attach(this, () =>
        {
            if (_confirming == null) return false;
            OnKeep(this, new RoutedEventArgs()); return true;
        });
        Opened += OnOpened; Closed += OnClosed;
        _session.Updated += OnSessionUpdated; Texts.Changed += OnLanguageChanged;
        Refresh();
    }

    private TagManagerRow? Selected => _list.SelectedItem as TagManagerRow;

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e) { if (!_refreshing) Refresh(); }

    private void OnOpened(object? sender, EventArgs e) { _list.Focus(); }
    private void OnSessionUpdated() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void OnSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        // A different tag: never carry a pending delete question or an edited name over to it.
        _confirming = null;
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

        TagId? selected = Selected?.Id;
        _refreshing = true;
        try
        {
            TagSortChoice.Localize(_sort);
            IReadOnlyList<TagManagerRow> rows = TagManagerRow.Sorted(_session.Rows, TagSortChoice.Of(_sort));
            _list.ItemsSource = rows;
            _list.SelectedItem = rows.FirstOrDefault(row => selected != null && row.Id == selected);
        }
        finally { _refreshing = false; }
        TagManagerRow? current = Selected;
        // The selected tag vanished (deleted here or elsewhere): drop its question and its draft name.
        if (current == null) { _confirming = null; if (selected != null) _name.Text = string.Empty; }
        else if (_confirming != null && _confirming != current.Id) _confirming = null;

        bool ready = _session.Snapshot != null && !_session.Busy;
        _name.IsEnabled = ready && current != null;
        _rename.IsEnabled = ready && current != null;
        _delete.IsEnabled = ready && current != null && _confirming == null;
        _confirm.IsVisible = _confirming != null && current != null;
        if (_confirm.IsVisible)
            _confirmText.Text = Texts.T("tags.manage.confirm", current!.Name, current.Threads, current.Media);
        _confirmDelete.IsEnabled = _keep.IsEnabled = ready;
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
        _name.RemoveHandler(KeyDownEvent, OnNameKey);
        Opened -= OnOpened; Closed -= OnClosed;
    }
}
