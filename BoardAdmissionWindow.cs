using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using MeowChan.Admission;
using MeowChan.Desktop.Localization;

namespace MeowChan;

internal sealed record BoardAdmissionSelection(IReadOnlyDictionary<string, BoardAdmissionPolicy> Policies, bool DirectFuture,
    IReadOnlyDictionary<string, string[]>? Reactivations = null);

/// <summary>A detached draft. Only its caller may commit the returned selection.</summary>
internal sealed class BoardAdmissionWindow : Window
{
    private sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }
    private sealed class Draft
    {
        public required string Url;
        public required BoardAdmissionPolicy Initial;
        public List<RuleEditor> Editors = new();
        public Dictionary<string, ThreadAdmissionException> Exceptions = new(StringComparer.Ordinal);
        public HashSet<string> Reactivated = new(StringComparer.Ordinal);
        public List<PreviewRow> Rows = new();
        public required StackPanel RulePanel;
        public required TextBlock Summary;
        public required TextBlock Matches;
        public required Button Reinclude;
        public required TextBlock Status;
        public required ListBox List;
        public required Button Retry;
        public bool Loaded;
        public bool Loading;
    }
    private sealed record RuleEditor(ComboBox Field, ComboBox Action, TextBox Text, Control Control);
    private sealed class PreviewRow : INotifyPropertyChanged
    {
        public required BoardCatalogThread Thread;
        public required Action<bool> Choose;
        public required Action Reset;
        private bool _included;
        private string _reason = string.Empty;
        public bool Included { get => _included; set { if (_included != value) Choose(value); } }
        public string Title => "#" + Thread.Id + " · " + Thread.Title
            + (Thread.AlreadyWatching ? " · " + Texts.T(Thread.ManuallyWatching ? "wizard.manualWatch" : "wizard.existingWatch") : string.Empty);
        public string OpeningPost => Thread.OpeningPost.Length > 350 ? Thread.OpeningPost[..350] + "…" : Thread.OpeningPost;
        public string Reason => _reason;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(BoardAdmissionDecision decision)
        {
            _included = decision.Included;
            _reason = Texts.T("wizard.reason." + decision.Reason);
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
    }
    private readonly List<Draft> _drafts = new();
    private readonly Func<string, CancellationToken, Task<BoardAdmissionPreview>> _preview;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Button _apply;
    private readonly CheckBox _direct;
    private readonly TextBlock _total = new() { Name = "BoardAdmissionTotal", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private bool _closing;

    public BoardAdmissionWindow(IEnumerable<string> urls, Func<string, BoardAdmissionPolicy> readPolicy,
        Func<string, CancellationToken, Task<BoardAdmissionPreview>> preview, bool showWizard)
    {
        _preview = preview;
        Title = Texts.T("wizard.title"); Width = 980; Height = 720; MinWidth = 640; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var shell = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var intro = new StackPanel { Spacing = 8 };
        intro.Children.Add(new TextBlock { Text = Title, Classes = { "page-title" } });
        intro.Children.Add(Help(Texts.T("wizard.intro")));
        intro.Children.Add(new Expander { Header = Texts.T("wizard.rulesHelp"), Content = Help(Texts.T("wizard.semantics")) });
        shell.Children.Add(intro);
        var tabs = new TabControl { Name = "BoardAdmissionTabs" };
        var items = new List<TabItem>();
        foreach (string url in urls.Distinct(StringComparer.Ordinal))
        {
            var rulePanel = new StackPanel { Spacing = 8 };
            var summary = new TextBlock { Name = "BoardAdmissionSummary", FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            var matches = Help(string.Empty); matches.Name = "BoardAdmissionMatches"; matches.IsVisible = false;
            var reinclude = new Button { Name = "BoardAdmissionReinclude", IsEnabled = false, IsVisible = false };
            var status = Help(Texts.T("wizard.loading")); status.Name = "BoardAdmissionStatus";
            var list = new ListBox { Name = "BoardAdmissionPreview", ItemTemplate = MakeTemplate(), MinHeight = 160, MaxHeight = 400 };
            var retry = new Button { Name = "BoardAdmissionRetry", Content = Texts.T("wizard.retry"), IsVisible = false };
            var draft = new Draft { Url = url, Initial = readPolicy(url), RulePanel = rulePanel, Summary = summary,
                Status = status, List = list, Matches = matches, Reinclude = reinclude, Retry = retry };
            reinclude.Click += (_, _) => ReincludeMatches(draft);
            draft.Exceptions = new(draft.Initial.Exceptions, StringComparer.Ordinal);
            _drafts.Add(draft);
            var header = new StackPanel { Spacing = 8 };
            header.Children.Add(new SelectableTextBlock { Text = url, Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap });
            header.Children.Add(new TextBlock { Text = Texts.T("wizard.rulesTitle"), Classes = { "section-title" } });
            var add = new Button { Name = "BoardAdmissionAddRule", Content = Texts.T("wizard.ruleAdd") };
            add.Click += (_, _) => AddRule(draft, new(ThreadTextField.TitleAndOpeningPost, ThreadTextAction.Exclude, string.Empty));
            var clear = new Button { Name = "BoardAdmissionClearTextRules", Content = Texts.T("wizard.clearTextRules") };
            AutomationProperties.SetName(clear, Texts.T("wizard.clearTextRules"));
            AutomationProperties.SetHelpText(clear, Texts.T("wizard.clearTextRulesHint"));
            clear.Click += (_, _) => ClearTextRules(draft);
            var ruleActions = new WrapPanel { Orientation = Orientation.Horizontal, Classes = { "workspace-toolbar" } };
            ruleActions.Children.Add(add); ruleActions.Children.Add(clear);
            header.Children.Add(ruleActions);
            header.Children.Add(Help(Texts.T("wizard.clearTextRulesHint")));
            header.Children.Add(new ScrollViewer { Content = rulePanel, MaxHeight = 180 });
            var previewHeader = new StackPanel { Spacing = 8 };
            previewHeader.Children.Add(new TextBlock { Text = Texts.T("wizard.previewTitle"), Classes = { "section-title" } });
            previewHeader.Children.Add(status); previewHeader.Children.Add(summary); previewHeader.Children.Add(matches); previewHeader.Children.Add(reinclude);
            retry.Click += async (_, _) => await LoadAsync(draft);
            previewHeader.Children.Add(retry);
            var previewPanel = new StackPanel { Spacing = 8 };
            previewPanel.Children.Add(previewHeader); previewPanel.Children.Add(list);
            var content = new StackPanel { Spacing = 16, Margin = new Thickness(0, 12, 0, 0) };
            content.Children.Add(header); content.Children.Add(previewPanel);
            items.Add(new TabItem { Header = new Uri(url).Host + new Uri(url).AbsolutePath,
                Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
            foreach (var rule in draft.Initial.Rules) AddRule(draft, rule);
        }
        tabs.ItemsSource = items; Grid.SetRow(tabs, 1); shell.Children.Add(tabs);
        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(_total);
        footer.Children.Add(Help(Texts.T("wizard.applyHint")));
        _direct = new CheckBox { Name = "BoardAdmissionDirectFuture", Content = Wrapped(Texts.T("wizard.directFuture")), IsChecked = !showWizard };
        AutomationProperties.SetName(_direct, Texts.T("wizard.directFuture"));
        footer.Children.Add(_direct);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Name = "BoardAdmissionCancel", Content = Texts.T("wizard.cancel") };
        cancel.Click += (_, _) => Close();
        _apply = new Button { Name = "BoardAdmissionApply", Content = Texts.T("wizard.apply"), IsEnabled = false };
        AutomationProperties.SetName(_apply, Texts.T("wizard.apply"));
        AutomationProperties.SetHelpText(_apply, Texts.T("wizard.applyHint"));
        _apply.Classes.Add("primary"); _apply.Click += (_, _) => Accept();
        buttons.Children.Add(cancel); buttons.Children.Add(_apply);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); shell.Children.Add(footer); Content = shell;
        Opened += async (_, _) => { foreach (var draft in _drafts) { if (_closing) break; await LoadAsync(draft); } };
        Closing += (_, _) => { _closing = true; _cancel.Cancel(); };
        KeyDown += (_, e) =>
        {
            if (e.Handled || MeowChan.Desktop.Commands.ShortcutService.IsCapturing) return;
            if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); }
            else MeowChan.Desktop.Commands.CommandBindings.RouteVisibleActions(this, e);
        };
        Opened += (_, _) => MeowChan.Desktop.Commands.LegacyCommandAdapters.Register(this);
    }
    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Help(string text) => new() { Text = text, Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private static FuncDataTemplate<PreviewRow> MakeTemplate() => new((row, _) =>
    {
        if (row == null) return new TextBlock();
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(4, 8), DataContext = row };
        var include = new CheckBox { Name = "BoardAdmissionInclude", Content = Wrapped(row.Title) };
        AutomationProperties.SetName(include, row.Title);
        include.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(PreviewRow.Included)) { Mode = BindingMode.TwoWay });
        var reason = Help(row.Reason);
        reason.Bind(TextBlock.TextProperty, new Binding(nameof(PreviewRow.Reason)));
        var decision = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        reason.VerticalAlignment = VerticalAlignment.Center;
        decision.Children.Add(reason);
        var reset = new Button
        {
            Name = "BoardAdmissionResetChoice", Content = Texts.T("wizard.resetChoice"),
            FontSize = 11, Padding = new Thickness(8, 4), MinHeight = 32,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(reset, Texts.T("wizard.resetChoiceForThread", row.Title));
        AutomationProperties.SetHelpText(reset, Texts.T("wizard.resetChoiceHint"));
        reset.Click += (_, _) => row.Reset();
        Grid.SetColumn(reset, 1); decision.Children.Add(reset);
        panel.Children.Add(include); panel.Children.Add(decision);
        if (!string.IsNullOrWhiteSpace(row.OpeningPost)) panel.Children.Add(new TextBlock
        {
            Text = row.OpeningPost, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return panel;
    });
    private void AddRule(Draft draft, ThreadTextRule rule)
    {
        var fields = new[] { new Choice<ThreadTextField>(ThreadTextField.Title, Texts.T("wizard.titleField")), new Choice<ThreadTextField>(ThreadTextField.OpeningPost, Texts.T("wizard.opField")), new Choice<ThreadTextField>(ThreadTextField.TitleAndOpeningPost, Texts.T("wizard.bothField")) };
        var actions = new[] { new Choice<ThreadTextAction>(ThreadTextAction.Include, Texts.T("wizard.include")), new Choice<ThreadTextAction>(ThreadTextAction.Exclude, Texts.T("wizard.exclude")) };
        var field = new ComboBox { ItemsSource = fields, SelectedItem = fields.First(item => item.Value == rule.Field), MinWidth = 145 };
        var action = new ComboBox { ItemsSource = actions, SelectedItem = actions.First(item => item.Value == rule.Action), MinWidth = 110 };
        var text = new TextBox { Name = "BoardAdmissionRuleText", Text = rule.Contains, PlaceholderText = Texts.T("wizard.contains") };
        AutomationProperties.SetName(field, Texts.T("wizard.ruleField"));
        AutomationProperties.SetName(action, Texts.T("wizard.ruleAction"));
        AutomationProperties.SetName(text, Texts.T("wizard.ruleText"));
        var remove = new Button { Name = "BoardAdmissionRemoveRule", Content = "×" }; Avalonia.Automation.AutomationProperties.SetName(remove, Texts.T("wizard.removeRule"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(field); Grid.SetColumn(action, 1); grid.Children.Add(action); Grid.SetColumn(text, 2); grid.Children.Add(text); Grid.SetColumn(remove, 3); grid.Children.Add(remove);
        var editor = new RuleEditor(field, action, text, grid); draft.Editors.Add(editor); draft.RulePanel.Children.Add(grid);
        field.SelectionChanged += (_, _) => Refresh(draft); action.SelectionChanged += (_, _) => Refresh(draft);
        text.TextChanged += (_, _) => Refresh(draft);
        remove.Click += (_, _) => { draft.Editors.Remove(editor); draft.RulePanel.Children.Remove(grid); Refresh(draft); };
        if (_apply != null) Refresh(draft);
    }
    private void ClearTextRules(Draft draft)
    {
        draft.Editors.Clear();
        draft.RulePanel.Children.Clear();
        Refresh(draft);
    }
    private static bool RulesValid(Draft draft) => draft.Editors.All(editor => !string.IsNullOrWhiteSpace(editor.Text.Text));
    private bool CanApply => _drafts.All(draft => draft.Loaded && RulesValid(draft));
    private static BoardAdmissionPolicy Policy(Draft draft) => new(draft.Editors
        .Select(editor => new ThreadTextRule(((Choice<ThreadTextField>)editor.Field.SelectedItem!).Value,
            ((Choice<ThreadTextAction>)editor.Action.SelectedItem!).Value, editor.Text.Text ?? string.Empty)), draft.Exceptions);
    private void Refresh(Draft draft)
    {
        bool valid = RulesValid(draft);
        foreach (RuleEditor editor in draft.Editors)
        {
            bool invalid = string.IsNullOrWhiteSpace(editor.Text.Text);
            editor.Text.Classes.Set("workspace-invalid", invalid);
            AutomationProperties.SetHelpText(editor.Text, invalid ? Texts.T("wizard.invalid") : string.Empty);
        }
        draft.Summary.Classes.Set("workspace-error", !valid);
        draft.Summary.IsVisible = !valid || draft.Loaded;
        draft.Matches.IsVisible = valid && draft.Loaded;
        draft.Reinclude.IsVisible = valid && draft.Loaded;
        if (!valid) { draft.Summary.Text = Texts.T("wizard.invalid"); draft.Matches.Text = string.Empty; draft.Reinclude.IsEnabled = false; }
        else if (!draft.Loaded)
        {
            draft.Summary.Text = string.Empty; draft.Matches.Text = string.Empty; draft.Reinclude.IsEnabled = false;
        }
        else
        {
            var policy = Policy(draft); int included = 0;
            // Explain text matching separately from persisted exclusions. A matching
            // removed thread must remain visible and explicitly reactivatable.
            var rulesOnly = new BoardAdmissionPolicy(policy.Rules);
            bool Matches(PreviewRow row) => rulesOnly.Evaluate(row.Thread, honorSuppression: false).Included;
            foreach (var row in draft.Rows)
            {
                var decision = policy.Evaluate(row.Thread, !draft.Reactivated.Contains(row.Thread.Url)); row.Update(decision);
                if (decision.Included) included++;
            }
            draft.Summary.Text = Texts.T("wizard.summary", included, draft.Rows.Count - included);
            int removedMatches = draft.Rows.Count(row => Matches(row) && PreviouslyExcluded(draft, row));
            draft.Matches.Text = Texts.T("wizard.matches", draft.Rows.Count(Matches), removedMatches);
            draft.Reinclude.Content = Texts.T("wizard.reinclude", removedMatches);
            draft.Reinclude.IsEnabled = removedMatches > 0;
            PreviewRow[] ordered = draft.Rows.OrderByDescending(Matches).ThenByDescending(row => row.Included).ToArray();
            if (draft.List.ItemsSource is not IEnumerable<PreviewRow> current || !current.SequenceEqual(ordered))
                draft.List.ItemsSource = ordered;
        }
        RefreshTotal();
    }
    private void RefreshTotal()
    {
        bool ready = CanApply;
        _apply.IsEnabled = ready;
        int loaded = _drafts.Count(draft => draft.Loaded);
        int invalid = _drafts.Count(draft => !RulesValid(draft));
        _total.Text = ready
            ? Texts.T("wizard.batchSummary", loaded, _drafts.Count, _drafts.Sum(draft => draft.Rows.Count(row => row.Included)))
            : Texts.T("wizard.batchPending", loaded, _drafts.Count, invalid);
        AutomationProperties.SetHelpText(_apply, ready ? Texts.T("wizard.applyHint") : _total.Text + " " + Texts.T("wizard.applyHint"));
    }
    private static bool PreviouslyExcluded(Draft draft, PreviewRow row) =>
        row.Thread.Suppressed && !draft.Reactivated.Contains(row.Thread.Url)
        || draft.Exceptions.TryGetValue(row.Thread.Url, out var choice) && choice == ThreadAdmissionException.Exclude;

    private void ReincludeMatches(Draft draft)
    {
        var rulesOnly = new BoardAdmissionPolicy(Policy(draft).Rules);
        foreach (var row in draft.Rows.Where(row => PreviouslyExcluded(draft, row)
            && rulesOnly.Evaluate(row.Thread, honorSuppression: false).Included))
        {
            draft.Exceptions[row.Thread.Url] = ThreadAdmissionException.Include;
            draft.Reactivated.Add(row.Thread.Url);
        }
        Refresh(draft);
    }
    private async Task LoadAsync(Draft draft)
    {
        if (draft.Loading || _closing) return;
        draft.Loading = true;
        draft.Loaded = false; draft.Status.Text = Texts.T("wizard.loading");
        draft.Status.Classes.Set("workspace-error", false);
        Refresh(draft);
        draft.Retry.IsEnabled = false; draft.List.IsEnabled = false;
        try
        {
            var preview = await _preview(draft.Url, _cancel.Token);
            if (_closing) return;
            draft.Rows = preview.Threads.Select(thread => new PreviewRow
            {
                Thread = thread,
                Choose = include => { draft.Exceptions[thread.Url] = include ? ThreadAdmissionException.Include : ThreadAdmissionException.Exclude; if (include) draft.Reactivated.Add(thread.Url); else draft.Reactivated.Remove(thread.Url); Refresh(draft); },
                Reset = () => { draft.Exceptions.Remove(thread.Url); draft.Reactivated.Remove(thread.Url); Refresh(draft); }
            }).ToList();
            draft.List.ItemsSource = draft.Rows;
            draft.Loaded = true; draft.Status.Text = draft.Rows.Count == 0 ? Texts.T("wizard.empty") : Texts.T("wizard.loaded", draft.Rows.Count);
            draft.Retry.IsVisible = false; draft.List.IsEnabled = true;
            Refresh(draft);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closing)
            {
                draft.Status.Text = Texts.T("wizard.failed", exception.Message);
                draft.Status.Classes.Set("workspace-error", true);
                draft.Retry.IsVisible = true;
                RefreshTotal();
            }
        }
        finally { draft.Loading = false; draft.Retry.IsEnabled = true; }
    }
    private void Accept()
    {
        if (!CanApply) return;
        Close(new BoardAdmissionSelection(_drafts.ToDictionary(draft => draft.Url, Policy, StringComparer.Ordinal), _direct.IsChecked == true,
            _drafts.ToDictionary(draft => draft.Url, draft => draft.Reactivated.ToArray(), StringComparer.Ordinal)));
    }
}
