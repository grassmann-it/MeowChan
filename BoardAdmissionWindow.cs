using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
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
    private bool _closing;

    public BoardAdmissionWindow(IEnumerable<string> urls, Func<string, BoardAdmissionPolicy> readPolicy,
        Func<string, CancellationToken, Task<BoardAdmissionPreview>> preview, bool showWizard)
    {
        _preview = preview;
        Title = Texts.T("wizard.title"); Width = 980; Height = 720; MinWidth = 640; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var shell = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var intro = new StackPanel { Spacing = 7 };
        intro.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeight.SemiBold });
        intro.Children.Add(Wrapped(Texts.T("wizard.intro")));
        intro.Children.Add(Wrapped(Texts.T("wizard.semantics")));
        shell.Children.Add(intro);
        var tabs = new TabControl { Name = "BoardAdmissionTabs" };
        var items = new List<TabItem>();
        foreach (string url in urls.Distinct(StringComparer.Ordinal))
        {
            var rulePanel = new StackPanel { Spacing = 6 };
            var summary = new TextBlock { Name = "BoardAdmissionSummary", FontWeight = FontWeight.SemiBold, FontSize = 16 };
            var matches = Wrapped(string.Empty); matches.Name = "BoardAdmissionMatches";
            var reinclude = new Button { Name = "BoardAdmissionReinclude", IsEnabled = false };
            var status = Wrapped(Texts.T("wizard.loading"));
            var list = new ListBox { Name = "BoardAdmissionPreview", ItemTemplate = MakeTemplate() };
            var draft = new Draft { Url = url, Initial = readPolicy(url), RulePanel = rulePanel, Summary = summary,
                Status = status, List = list, Matches = matches, Reinclude = reinclude };
            reinclude.Click += (_, _) => ReincludeMatches(draft);
            draft.Exceptions = new(draft.Initial.Exceptions, StringComparer.Ordinal);
            _drafts.Add(draft);
            var header = new StackPanel { Spacing = 7 };
            header.Children.Add(Wrapped(url));
            var add = new Button { Name = "BoardAdmissionAddRule", Content = Texts.T("wizard.ruleAdd") };
            add.Click += (_, _) => AddRule(draft, new(ThreadTextField.TitleAndOpeningPost, ThreadTextAction.Exclude, string.Empty));
            header.Children.Add(add);
            header.Children.Add(new ScrollViewer { Content = rulePanel, MaxHeight = 180 });
            header.Children.Add(summary); header.Children.Add(matches); header.Children.Add(reinclude); header.Children.Add(status);
            var retry = new Button { Content = Texts.T("wizard.retry") };
            retry.Click += async (_, _) => { retry.IsEnabled = false; try { await LoadAsync(draft); } finally { if (!_closing) retry.IsEnabled = true; } };
            header.Children.Add(retry);
            var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
            content.Children.Add(header); Grid.SetRow(list, 1); content.Children.Add(list);
            items.Add(new TabItem { Header = new Uri(url).Host + new Uri(url).AbsolutePath, Content = content });
            foreach (var rule in draft.Initial.Rules) AddRule(draft, rule);
        }
        tabs.ItemsSource = items; Grid.SetRow(tabs, 1); shell.Children.Add(tabs);
        var footer = new StackPanel { Spacing = 7 };
        footer.Children.Add(Wrapped(Texts.T("wizard.allHint")));
        _direct = new CheckBox { Content = Texts.T("wizard.directFuture"), IsChecked = !showWizard };
        footer.Children.Add(_direct);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Name = "BoardAdmissionCancel", Content = Texts.T("wizard.cancel") };
        cancel.Click += (_, _) => Close();
        var all = new Button { Name = "BoardAdmissionAll", Content = Texts.T("wizard.all") };
        all.Click += (_, _) => Accept(true);
        _apply = new Button { Name = "BoardAdmissionApply", Content = Texts.T("wizard.apply"), IsEnabled = false };
        _apply.Classes.Add("primary"); _apply.Click += (_, _) => Accept(false);
        buttons.Children.Add(cancel); buttons.Children.Add(all); buttons.Children.Add(_apply);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); shell.Children.Add(footer); Content = shell;
        Opened += async (_, _) => { foreach (var draft in _drafts) { if (_closing) break; await LoadAsync(draft); } };
        Closing += (_, _) => { _closing = true; _cancel.Cancel(); };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static FuncDataTemplate<PreviewRow> MakeTemplate() => new((row, _) =>
    {
        if (row == null) return new TextBlock();
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(3, 6), DataContext = row };
        var include = new CheckBox { Name = "BoardAdmissionInclude", Content = Wrapped(row.Title) };
        include.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(PreviewRow.Included)) { Mode = BindingMode.TwoWay });
        var reason = Wrapped(row.Reason);
        reason.Bind(TextBlock.TextProperty, new Binding(nameof(PreviewRow.Reason)));
        panel.Children.Add(include); panel.Children.Add(reason);
        if (!string.IsNullOrWhiteSpace(row.OpeningPost)) panel.Children.Add(new TextBlock
        {
            Text = row.OpeningPost, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var reset = new Button { Content = Texts.T("wizard.resetChoice"), FontSize = 11, Padding = new Thickness(5, 2) };
        reset.Click += (_, _) => row.Reset(); panel.Children.Add(reset);
        return panel;
    });
    private void AddRule(Draft draft, ThreadTextRule rule)
    {
        var fields = new[] { new Choice<ThreadTextField>(ThreadTextField.Title, Texts.T("wizard.titleField")), new Choice<ThreadTextField>(ThreadTextField.OpeningPost, Texts.T("wizard.opField")), new Choice<ThreadTextField>(ThreadTextField.TitleAndOpeningPost, Texts.T("wizard.bothField")) };
        var actions = new[] { new Choice<ThreadTextAction>(ThreadTextAction.Include, Texts.T("wizard.include")), new Choice<ThreadTextAction>(ThreadTextAction.Exclude, Texts.T("wizard.exclude")) };
        var field = new ComboBox { ItemsSource = fields, SelectedItem = fields.First(item => item.Value == rule.Field), MinWidth = 145 };
        var action = new ComboBox { ItemsSource = actions, SelectedItem = actions.First(item => item.Value == rule.Action), MinWidth = 110 };
        var text = new TextBox { Name = "BoardAdmissionRuleText", Text = rule.Contains, PlaceholderText = Texts.T("wizard.contains") };
        var remove = new Button { Content = "×" }; Avalonia.Automation.AutomationProperties.SetName(remove, Texts.T("wizard.removeRule"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(field); Grid.SetColumn(action, 1); grid.Children.Add(action); Grid.SetColumn(text, 2); grid.Children.Add(text); Grid.SetColumn(remove, 3); grid.Children.Add(remove);
        var editor = new RuleEditor(field, action, text, grid); draft.Editors.Add(editor); draft.RulePanel.Children.Add(grid);
        field.SelectionChanged += (_, _) => Refresh(draft); action.SelectionChanged += (_, _) => Refresh(draft);
        text.TextChanged += (_, _) => Refresh(draft);
        remove.Click += (_, _) => { draft.Editors.Remove(editor); draft.RulePanel.Children.Remove(grid); Refresh(draft); };
        if (_apply != null) Refresh(draft);
    }
    private static BoardAdmissionPolicy Policy(Draft draft, bool all = false) => new(all ? Array.Empty<ThreadTextRule>() : draft.Editors
        .Select(editor => new ThreadTextRule(((Choice<ThreadTextField>)editor.Field.SelectedItem!).Value,
            ((Choice<ThreadTextAction>)editor.Action.SelectedItem!).Value, editor.Text.Text ?? string.Empty)), draft.Exceptions);
    private void Refresh(Draft draft)
    {
        bool valid = draft.Editors.All(editor => !string.IsNullOrWhiteSpace(editor.Text.Text));
        if (!valid) { draft.Summary.Text = Texts.T("wizard.invalid"); draft.Matches.Text = string.Empty; draft.Reinclude.IsEnabled = false; }
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
        _apply.IsEnabled = _drafts.All(item => item.Loaded && item.Editors.All(editor => !string.IsNullOrWhiteSpace(editor.Text.Text)));
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
        draft.Loaded = false; draft.Status.Text = Texts.T("wizard.loading"); _apply.IsEnabled = false;
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
            draft.Loaded = true; draft.Status.Text = draft.Rows.Count == 0 ? Texts.T("wizard.empty") : string.Empty;
            Refresh(draft);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception exception) { if (!_closing) draft.Status.Text = Texts.T("wizard.failed", exception.Message); }
        finally { draft.Loading = false; }
    }
    private void Accept(bool all)
    {
        if (!all && !_apply.IsEnabled) return;
        Close(new BoardAdmissionSelection(_drafts.ToDictionary(draft => draft.Url, draft => Policy(draft, all), StringComparer.Ordinal), _direct.IsChecked == true,
            _drafts.ToDictionary(draft => draft.Url, draft => draft.Reactivated.ToArray(), StringComparer.Ordinal)));
    }
}
