using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

/// <summary>
/// "Post filters…" (plan P2): the rules (add, edit, switch on and off, remove) and the individually hidden posts (show again,
/// placeholder or hide completely). A code dialog like the hash search; all logic is in <see cref="PostFilterDialogModel"/>.
/// </summary>
internal sealed class PostFilterDialog : Window
{
    private static readonly PostFilterField[] Fields = { PostFilterField.Text, PostFilterField.Subject, PostFilterField.Filename, PostFilterField.Md5 };
    private static readonly PostFilterMatch[] Matches = { PostFilterMatch.Contains, PostFilterMatch.Exact };
    private static readonly PostFilterAction[] Actions = { PostFilterAction.Stub, PostFilterAction.Hide, PostFilterAction.Highlight };
    private static readonly HiddenPostsAction[] HiddenActions = { HiddenPostsAction.Stub, HiddenPostsAction.Hide };

    private readonly PostFilterDialogModel _model;
    private readonly CancellationToken _lifetime;
    private readonly WindowChromeBar _chrome = new() { Name = "PostFilterChrome" };
    private readonly TextBlock _heading = Label("PostFilterHeading");
    private readonly TextBlock _hint = Label("PostFilterHint");
    private readonly TextBlock _notice = Label("PostFilterNotice");
    private readonly Button _recover = new() { Name = "PostFilterRecover" };
    private readonly TextBlock _status = Label("PostFilterStatus");
    private readonly TabControl _tabs = new() { Name = "PostFilterTabs" };
    private readonly TabItem _rulesTab = new() { Name = "PostFilterRulesTab" };
    private readonly TabItem _postsTab = new() { Name = "PostFilterPostsTab" };
    private readonly TextBlock _fieldLabel = Label("PostFilterFieldLabel");
    private readonly ComboBox _field = new() { Name = "PostFilterField", MinWidth = 160 };
    private readonly TextBlock _matchLabel = Label("PostFilterMatchLabel");
    private readonly ComboBox _match = new() { Name = "PostFilterMatch", MinWidth = 160 };
    private readonly TextBlock _patternLabel = Label("PostFilterPatternLabel");
    private readonly TextBox _pattern = new() { Name = "PostFilterPattern", MaxLength = 2 * PostFilterRules.MaximumPatternCodepoints };
    private readonly TextBlock _patternHint = Label("PostFilterPatternHint");
    private readonly TextBlock _draftError = Label("PostFilterDraftError");
    private readonly TextBlock _actionLabel = Label("PostFilterActionLabel");
    private readonly ComboBox _action = new() { Name = "PostFilterAction", MinWidth = 160 };
    private readonly CheckBox _replies = new() { Name = "PostFilterReplies" };
    private readonly TextBlock _scopeLabel = Label("PostFilterScopeLabel");
    private readonly ComboBox _scope = new() { Name = "PostFilterScope", MinWidth = 160 };
    private readonly TextBlock _editing = Label("PostFilterEditing");
    private readonly Button _add = new() { Name = "PostFilterAdd" };
    private readonly Button _save = new() { Name = "PostFilterSave" };
    private readonly Button _cancelEdit = new() { Name = "PostFilterCancelEdit" };
    private readonly StackPanel _rules = new() { Name = "PostFilterRules", Spacing = 6 };
    private readonly TextBlock _hiddenActionLabel = Label("PostFilterHiddenActionLabel");
    private readonly ComboBox _hiddenAction = new() { Name = "PostFilterHiddenAction", MinWidth = 200 };
    private readonly StackPanel _posts = new() { Name = "PostFilterPosts", Spacing = 6 };
    private readonly Button _close = new() { Name = "PostFilterClose" };
    private bool _refreshing;
    private bool _closed;

    internal PostFilterDialogModel Model => _model;

    internal PostFilterDialog(PostFilterDialogModel model, CancellationToken lifetime = default)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _lifetime = lifetime;
        Name = "PostFilterDialog";
        Width = 720; Height = 640; MinWidth = 360; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.Apply(this);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _heading.Classes.Add("section-title");
        _hint.Classes.Add("muted");
        _patternHint.Classes.Add("muted");
        _draftError.Classes.Add("workspace-error");
        _editing.Classes.Add("muted");
        AutomationProperties.SetLiveSetting(_editing, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_draftError, AutomationLiveSetting.Assertive);

        StackPanel header = new() { Spacing = 8 };
        header.Children.Add(_heading); header.Children.Add(_hint);
        header.Children.Add(_notice); header.Children.Add(_recover); header.Children.Add(_status);

        WrapPanel choices = new() { ItemSpacing = 12, LineSpacing = 8 };
        choices.Children.Add(Pair(_fieldLabel, _field)); choices.Children.Add(Pair(_matchLabel, _match));
        choices.Children.Add(Pair(_actionLabel, _action)); choices.Children.Add(Pair(_scopeLabel, _scope));
        WrapPanel draftButtons = new() { ItemSpacing = 8, LineSpacing = 8 };
        draftButtons.Children.Add(_add); draftButtons.Children.Add(_save); draftButtons.Children.Add(_cancelEdit);
        StackPanel draft = new() { Spacing = 6 };
        draft.Children.Add(_editing); draft.Children.Add(_patternLabel); draft.Children.Add(_pattern); draft.Children.Add(_patternHint); draft.Children.Add(_draftError);
        draft.Children.Add(choices); draft.Children.Add(_replies); draft.Children.Add(draftButtons);
        StackPanel rulesPage = new() { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        rulesPage.Children.Add(draft); rulesPage.Children.Add(new Separator()); rulesPage.Children.Add(_rules);
        _rulesTab.Content = rulesPage;
        StackPanel postsPage = new() { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        postsPage.Children.Add(Pair(_hiddenActionLabel, _hiddenAction)); postsPage.Children.Add(_posts);
        _postsTab.Content = postsPage;
        _tabs.Items.Add(_rulesTab); _tabs.Items.Add(_postsTab);
        StackPanel body = new() { Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_tabs);
        WrapPanel buttons = new() { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_close);
        Grid frame = DialogFrame.Build(header, body, buttons);
        KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Continue);
        Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(_chrome);
        grid.Children.Add(frame); Grid.SetRow(frame, 1);
        Content = grid;

        _field.SelectionChanged += (_, _) => { if (!_refreshing && _field.SelectedIndex >= 0) { _model.Field = Fields[_field.SelectedIndex]; Refresh(); } };
        _match.SelectionChanged += (_, _) => { if (!_refreshing && _match.SelectedIndex >= 0) { _model.Match = Matches[_match.SelectedIndex]; Refresh(); } };
        _action.SelectionChanged += (_, _) => { if (!_refreshing && _action.SelectedIndex >= 0) { _model.Action = Actions[_action.SelectedIndex]; Refresh(); } };
        _scope.SelectionChanged += (_, _) => { if (!_refreshing && _scope.SelectedIndex >= 0) { _model.Scope = _model.Scopes[_scope.SelectedIndex]; Refresh(); } };
        _hiddenAction.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _hiddenAction.SelectedIndex >= 0) _ = _model.SetHiddenActionAsync(HiddenActions[_hiddenAction.SelectedIndex], _lifetime);
        };
        _replies.IsCheckedChanged += (_, _) => { if (!_refreshing) _model.Replies = _replies.IsChecked == true; };
        _pattern.PropertyChanged += OnPatternChanged;
        _add.Click += async (_, _) => await AddAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _cancelEdit.Click += (_, _) => CancelEdit();
        _recover.Click += async (_, _) => await _model.RecoverAsync(_lifetime);
        _close.Click += (_, _) => Close();
        _model.Changed += OnModelChanged;
        Texts.Changed += OnTextsChanged;
        KeyDown += OnKeyDown;
        Opened += (_, _) => { if (!_closed) { _chrome.Attach(this); _pattern.Focus(); } };
        Closed += (_, _) => Detach();
        Refresh();
    }

    private static TextBlock Label(string name) => new() { Name = name, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Pair(TextBlock label, Control input)
    {
        StackPanel pair = new() { Spacing = 4 };
        pair.Children.Add(label); pair.Children.Add(input);
        AutomationProperties.SetLabeledBy(input, label);
        return pair;
    }

    private async Task AddAsync()
    {
        if (await _model.AddAsync(_lifetime) && !_closed) { _pattern.Text = string.Empty; _pattern.Focus(); }
    }

    private async Task SaveAsync()
    {
        if (await _model.SaveEditAsync(_lifetime) && !_closed) _pattern.Focus();
    }

    private void CancelEdit()
    {
        _model.CancelEdit();
        if (!_closed) _pattern.Focus();
    }

    private void BeginEdit(Guid id)
    {
        if (!_model.BeginEdit(id) || _closed) return;
        _tabs.SelectedItem = _rulesTab;
        _pattern.Focus();
        _pattern.CaretIndex = _pattern.Text?.Length ?? 0;
    }

    private void OnPatternChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_refreshing || e.Property != TextBox.TextProperty) return;
        _model.Pattern = _pattern.Text ?? string.Empty;
        RefreshDraft();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Escape first leaves the edit of a rule (back to adding one), and only then closes the dialog.
        if (e.Key == Key.Escape) { e.Handled = true; if (_model.IsEditing) CancelEdit(); else Close(); }
        // Enter in the search text is the keyboard form of "Add rule", or of "Save changes" while a rule is edited.
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && ReferenceEquals(e.Source, _pattern))
        {
            if (_model.CanSave) { e.Handled = true; _ = SaveAsync(); }
            else if (_model.CanAdd) { e.Handled = true; _ = AddAsync(); }
        }
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private static void Fill<T>(ComboBox box, IEnumerable<T> values, Func<T, string> label, int selected)
    {
        string[] items = values.Select(label).ToArray();
        if (box.ItemsSource is not string[] current || !current.SequenceEqual(items)) box.ItemsSource = items;
        if (box.SelectedIndex != selected) box.SelectedIndex = selected;
    }

    private static void ButtonText(Button button, string text)
    {
        if (!Equals(button.Content, text)) button.Content = text;
        AutomationProperties.SetName(button, text);
    }

    private void RefreshDraft()
    {
        string? error = _model.DraftError;
        _draftError.Text = error ?? string.Empty;
        _draftError.IsVisible = error != null;
        bool editing = _model.IsEditing;
        _add.IsVisible = !editing;
        _add.IsEnabled = _model.CanAdd;
        _save.IsVisible = editing;
        _save.IsEnabled = _model.CanSave;
        _cancelEdit.IsVisible = editing;
        _cancelEdit.IsEnabled = !_model.IsBusy;
    }

    internal void Refresh()
    {
        if (_closed) return;
        _refreshing = true;
        try
        {
            Title = Texts.T("postFilter.title"); _heading.Text = Title;
            _hint.Text = Texts.T("postFilter.hint");
            string? notice = _model.StoreNotice;
            _notice.Text = notice ?? string.Empty;
            _notice.IsVisible = notice != null;
            _notice.Classes.Set("workspace-error", notice != null);
            ButtonText(_recover, Texts.T("postFilter.recovery.restore"));
            _recover.IsVisible = _model.CanRecover;
            _status.Text = _model.Status ?? string.Empty;
            _status.IsVisible = _model.Status != null;
            _status.Classes.Set("workspace-error", _model.StatusIsError);
            _rulesTab.Header = Texts.T("postFilter.tab.rules");
            _postsTab.Header = Texts.T("postFilter.tab.posts");

            _fieldLabel.Text = Texts.T("postFilter.field");
            Fill(_field, Fields, PostFilterDialogModel.FieldLabel, Array.IndexOf(Fields, _model.Field));
            _matchLabel.Text = Texts.T("postFilter.match");
            Fill(_match, Matches, PostFilterDialogModel.MatchLabel, _model.MatchSelectable ? Array.IndexOf(Matches, _model.Match) : 1);
            _match.IsEnabled = _model.MatchSelectable;
            _patternLabel.Text = Texts.T("postFilter.pattern");
            AutomationProperties.SetName(_pattern, _patternLabel.Text);
            _patternHint.Text = Texts.T("postFilter.pattern.hint");
            AutomationProperties.SetHelpText(_pattern, _patternHint.Text);
            if (_pattern.Text != _model.Pattern) _pattern.Text = _model.Pattern;
            _actionLabel.Text = Texts.T("postFilter.action");
            Fill(_action, Actions, PostFilterDialogModel.ActionLabel, Array.IndexOf(Actions, _model.Action));
            _scopeLabel.Text = Texts.T("postFilter.scope");
            Fill(_scope, _model.Scopes, _model.ScopeLabel, Math.Max(0, _model.Scopes.ToList().IndexOf(_model.Scope)));
            _replies.Content = Texts.T("postFilter.replies");
            _replies.IsChecked = _model.Replies && _model.RepliesSelectable;
            _replies.IsEnabled = _model.RepliesSelectable;
            ButtonText(_add, Texts.T("postFilter.add"));
            ButtonText(_save, Texts.T("postFilter.save"));
            ButtonText(_cancelEdit, Texts.T("postFilter.cancelEdit"));
            string? editingLabel = _model.EditingLabel;
            _editing.Text = editingLabel == null ? string.Empty : Texts.T("postFilter.editing", editingLabel);
            _editing.IsVisible = editingLabel != null;
            RefreshDraft();
            bool idle = !_model.IsBusy && _model.CanWrite;
            RenderRules(idle);

            _hiddenActionLabel.Text = Texts.T("postFilter.hiddenAction");
            Fill(_hiddenAction, HiddenActions, action => Texts.T(action == HiddenPostsAction.Hide ? "postFilter.hiddenAction.hide" : "postFilter.hiddenAction.stub"),
                Array.IndexOf(HiddenActions, _model.HiddenAction));
            _hiddenAction.IsEnabled = idle;
            RenderPosts(idle);
            ButtonText(_close, Texts.T("postFilter.close"));
        }
        finally { _refreshing = false; }
    }

    private void RenderRules(bool idle)
    {
        _rules.Children.Clear();
        IReadOnlyList<PostFilterDialogModel.RuleRow> rows = _model.Rules;
        if (rows.Count == 0) { _rules.Children.Add(new TextBlock { Text = Texts.T("postFilter.noRules"), Classes = { "muted" } }); return; }
        Guid? editingId = _model.EditingRule?.Id;
        foreach (PostFilterDialogModel.RuleRow row in rows)
        {
            Guid id = row.Rule.Id;
            CheckBox enabled = new()
            {
                Content = new TextBlock { Text = row.Label, TextWrapping = TextWrapping.Wrap }, IsChecked = row.Supported && row.Rule.Enabled,
                IsEnabled = idle && row.Supported && !_model.IsEditingRule(id), Tag = id
            };
            AutomationProperties.SetName(enabled, Texts.T("postFilter.enabled") + ": " + row.Label);
            enabled.IsCheckedChanged += (_, _) => { if (!_refreshing) _ = _model.SetEnabledAsync(id, enabled.IsChecked == true, _lifetime); };
            Button edit = new() { Content = Texts.T("postFilter.edit"), IsEnabled = idle && row.Supported && editingId != id, Tag = id, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(edit, Texts.T("postFilter.edit") + ": " + row.Label);
            if (!row.Supported)
            {
                // Read-only rule: the disabled button says why, for the pointer and for screen readers.
                ToolTip.SetTip(edit, Texts.T("postFilter.editUnsupported"));
                AutomationProperties.SetHelpText(edit, Texts.T("postFilter.editUnsupported"));
            }
            edit.Click += (_, _) => BeginEdit(id);
            Button remove = new() { Content = Texts.T("postFilter.remove"), IsEnabled = idle, Tag = id, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, Texts.T("postFilter.remove") + ": " + row.Label);
            remove.Click += async (_, _) => await _model.RemoveAsync(id, _lifetime);
            StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(edit); buttons.Children.Add(remove);
            TextBlock details = new() { Text = row.Details, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
            StackPanel text = new() { Spacing = 2 };
            text.Children.Add(enabled); text.Children.Add(details);
            Grid line = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Tag = id };
            line.Children.Add(text);
            line.Children.Add(buttons); Grid.SetColumn(buttons, 1);
            _rules.Children.Add(line);
        }
    }

    private void RenderPosts(bool idle)
    {
        _posts.Children.Clear();
        IReadOnlyList<PostFilterDialogModel.HiddenRow> rows = _model.HiddenPosts;
        if (rows.Count == 0) { _posts.Children.Add(new TextBlock { Text = Texts.T("postFilter.noPosts"), TextWrapping = TextWrapping.Wrap, Classes = { "muted" } }); return; }
        foreach (PostFilterDialogModel.HiddenRow row in rows)
        {
            HiddenPost post = row.Post;
            Button show = new() { Content = Texts.T("command.post.unhide"), IsEnabled = idle, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(show, Texts.T("command.post.unhide") + ": " + row.Label);
            show.Click += async (_, _) => await _model.UnhideAsync(post, _lifetime);
            Grid line = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            line.Children.Add(new TextBlock { Text = row.Label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(show); Grid.SetColumn(show, 1);
            _posts.Children.Add(line);
        }
    }

    private void Detach()
    {
        if (_closed) return;
        _closed = true;
        _model.Changed -= OnModelChanged;
        Texts.Changed -= OnTextsChanged;
        _pattern.PropertyChanged -= OnPatternChanged;
        KeyDown -= OnKeyDown;
    }
}
