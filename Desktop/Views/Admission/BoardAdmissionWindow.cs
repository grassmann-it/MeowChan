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
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

internal sealed record BoardAdmissionSelection(IReadOnlyDictionary<string, BoardAdmissionPolicy> Policies, bool DirectFuture,
    IReadOnlyDictionary<string, string[]>? Reactivations = null);

/// <summary>A detached draft. Only its caller may commit the returned selection.</summary>
internal sealed class BoardAdmissionWindow : Window, IDisposable
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
        public string? Failure; // Provider/backend detail stays verbatim; only the host wrapper is localized.
        public bool Loaded;
        public bool Loading;
    }
    private sealed record RuleEditor(ComboBox Field, ComboBox Action, TextBox Text, Button Remove, Control Control);
    private sealed class PreviewRow : INotifyPropertyChanged
    {
        public required BoardCatalogThread Thread;
        public required Action<bool> Choose;
        public required Action Reset;
        private bool _included;
        private string _reasonKey = string.Empty;
        public bool Included { get => _included; set { if (_included != value) Choose(value); } }
        public string Title => "#" + Thread.Id + " · " + Thread.Title
            + (Thread.AlreadyWatching ? " · " + Texts.T(Thread.ManuallyWatching ? "wizard.manualWatch" : "wizard.existingWatch") : string.Empty);
        public string OpeningPost => Thread.OpeningPost.Length > 350 ? Thread.OpeningPost[..350] + "…" : Thread.OpeningPost;
        public string Reason => Texts.T(_reasonKey);
        public string ResetLabel => Texts.T("wizard.resetChoice");
        public string ResetName => Texts.T("wizard.resetChoiceForThread", Title);
        public string ResetHint => Texts.T("wizard.resetChoiceHint");
        public void RefreshTexts() => PropertyChanged?.Invoke(this, new(string.Empty));
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(BoardAdmissionDecision decision)
        {
            _included = decision.Included;
            _reasonKey = "wizard.reason." + decision.Reason;
            RefreshTexts();
        }
    }
    private readonly List<Draft> _drafts = new();
    private readonly Func<string, CancellationToken, Task<BoardAdmissionPreview>> _preview;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Button _apply;
    private readonly CheckBox _direct;
    private readonly TextBlock _total = new() { Name = "BoardAdmissionTotal", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly List<Action> _textUpdates = new();
    private bool _applyingTexts;
    private bool _disposed;
    private bool _closing;

    public BoardAdmissionWindow(IEnumerable<string> urls, Func<string, BoardAdmissionPolicy> readPolicy,
        Func<string, CancellationToken, Task<BoardAdmissionPreview>> preview, bool showWizard)
    {
        _preview = preview;
        Title = Texts.T("wizard.title"); Width = 980; Height = 720; MinWidth = 640; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Shared dialog frame: title and task stay fixed, the rules help and board tabs scroll,
        // the batch summary and joint actions stay fixed. Catalog failures stay tab-local next
        // to their Retry, so the frame's error strip is an empty slot.
        var intro = new StackPanel { Spacing = 8 };
        intro.Children.Add(Localized(new TextBlock { Classes = { "page-title" }, TextWrapping = TextWrapping.Wrap }, c => c.Text = Texts.T("wizard.title")));
        intro.Children.Add(Localized(Help(string.Empty), c => c.Text = Texts.T("wizard.intro")));
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Localized(new Expander { Content = Localized(Help(string.Empty), c => c.Text = Texts.T("wizard.semantics")) }, c => c.Header = Texts.T("wizard.rulesHelp")));
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
            var retry = Localized(new Button { Name = "BoardAdmissionRetry", IsVisible = false }, c => c.Content = Texts.T("wizard.retry"));
            var draft = new Draft { Url = url, Initial = readPolicy(url), RulePanel = rulePanel, Summary = summary,
                Status = status, List = list, Matches = matches, Reinclude = reinclude, Retry = retry };
            reinclude.Click += (_, _) => ReincludeMatches(draft);
            draft.Exceptions = new(draft.Initial.Exceptions, StringComparer.Ordinal);
            _drafts.Add(draft);
            var header = new StackPanel { Spacing = 8 };
            header.Children.Add(new SelectableTextBlock { Text = url, Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap });
            header.Children.Add(Localized(new TextBlock { Classes = { "section-title" } }, c => c.Text = Texts.T("wizard.rulesTitle")));
            var add = Localized(new Button { Name = "BoardAdmissionAddRule" }, c =>
            {
                c.Content = Texts.T("wizard.ruleAdd");
                RefreshCommandHelp(c, "admission.add-rule");
            });
            add.Click += (_, _) => AddRule(draft, new(ThreadTextField.TitleAndOpeningPost, ThreadTextAction.Exclude, string.Empty));
            var clear = Localized(new Button { Name = "BoardAdmissionClearTextRules" }, c =>
            {
                c.Content = Texts.T("wizard.clearTextRules");
                AutomationProperties.SetName(c, Texts.T("wizard.clearTextRules"));
                AutomationProperties.SetHelpText(c, Texts.T("wizard.clearTextRulesHint"));
            });
            clear.Click += (_, _) => ClearTextRules(draft);
            var ruleActions = new WrapPanel { Orientation = Orientation.Horizontal, Classes = { "workspace-toolbar" } };
            ruleActions.Children.Add(add); ruleActions.Children.Add(clear);
            header.Children.Add(ruleActions);
            header.Children.Add(Localized(Help(string.Empty), c => c.Text = Texts.T("wizard.clearTextRulesHint")));
            header.Children.Add(new ScrollViewer { Content = rulePanel, MaxHeight = 180 });
            var previewHeader = new StackPanel { Spacing = 8 };
            previewHeader.Children.Add(Localized(new TextBlock { Classes = { "section-title" } }, c => c.Text = Texts.T("wizard.previewTitle")));
            previewHeader.Children.Add(status); previewHeader.Children.Add(summary); previewHeader.Children.Add(matches); previewHeader.Children.Add(reinclude);
            retry.Click += async (_, _) => await LoadAsync(draft);
            previewHeader.Children.Add(retry);
            var previewPanel = new StackPanel { Spacing = 8 };
            previewPanel.Children.Add(previewHeader); previewPanel.Children.Add(list);
            var content = new StackPanel { Spacing = 16, Margin = new Thickness(0, 12, 0, 0) };
            content.Children.Add(header); content.Children.Add(previewPanel);
            // The frame body is the single vertical scroll for the active board.
            items.Add(new TabItem { Header = new Uri(url).Host + new Uri(url).AbsolutePath, Content = content });
            foreach (var rule in draft.Initial.Rules) AddRule(draft, rule);
        }
        tabs.ItemsSource = items; body.Children.Add(tabs);
        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(_total);
        footer.Children.Add(Localized(Help(string.Empty), c => c.Text = Texts.T("wizard.applyHint")));
        _direct = Localized(new CheckBox { Name = "BoardAdmissionDirectFuture", IsChecked = !showWizard }, c =>
        {
            c.Content = Wrapped(Texts.T("wizard.directFuture"));
            AutomationProperties.SetName(c, Texts.T("wizard.directFuture"));
        });
        footer.Children.Add(_direct);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Localized(new Button { Name = "BoardAdmissionCancel" }, c =>
        {
            c.Content = Texts.T("wizard.cancel");
            RefreshCommandHelp(c, "admission.cancel");
        });
        cancel.Click += (_, _) => Close();
        _apply = Localized(new Button { Name = "BoardAdmissionApply", IsEnabled = false }, c =>
        {
            c.Content = Texts.T("wizard.apply");
            AutomationProperties.SetName(c, Texts.T("wizard.apply"));
        });
        _apply.Classes.Add("primary"); _apply.Click += (_, _) => Accept();
        buttons.Children.Add(cancel); buttons.Children.Add(_apply);
        footer.Children.Add(buttons);
        Content = DialogFrame.Build(intro, new Border { Child = body, Classes = { "workspace-dialog-body" } }, footer,
            DialogFrame.ErrorStrip(new TextBlock()));
        Opened += async (_, _) => { foreach (var draft in _drafts) { if (_closing) break; await LoadAsync(draft); } };
        Texts.Changed += OnTextsChanged;
        ApplyTexts();
        Closed += (_, _) => Dispose();
        Closing += (_, _) => { if (!_disposed) { _closing = true; Texts.Changed -= OnTextsChanged; _cancel.Cancel(); } };
        KeyDown += (_, e) =>
        {
            if (e.Handled || MeowChan.Desktop.Commands.ShortcutService.IsCapturing) return;
            if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); }
            else MeowChan.Desktop.Commands.CommandBindings.RouteVisibleActions(this, e);
        };
        Opened += (_, _) => MeowChan.Desktop.Commands.LegacyCommandAdapters.Register(this);
    }
    private T Localized<T>(T control, Action<T> update) where T : Control
    {
        update(control);
        _textUpdates.Add(() => update(control));
        return control;
    }

    private static void RefreshCommandHelp(Control control, string id, bool preserveHelp = false)
    {
        // BindExisting (used by LegacyCommandAdapters) skips already registered
        // controls. Reuse its tooltip formatter without registering another action
        // or presentation observer; Localized reapplies this on language changes.
        string tip = MeowChan.Desktop.Commands.CommandBindings.Tooltip(id);
        ToolTip.SetTip(control, tip);
        if (!preserveHelp) AutomationProperties.SetHelpText(control, tip);
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closing && !_disposed)
            ApplyTexts();
    });

    private void ApplyTexts()
    {
        if (_closing || _disposed) return;
        Title = Texts.T("wizard.title");
        _applyingTexts = true;
        try
        {
            foreach (var update in _textUpdates) update();
            foreach (var draft in _drafts)
            {
                foreach (var editor in draft.Editors) ApplyEditorTexts(editor);
                foreach (var row in draft.Rows) row.RefreshTexts();
                RefreshCommandHelp(draft.Retry, "admission.retry-catalog");
                RefreshCommandHelp(draft.Reinclude, "admission.reinclude");
                draft.Status.Text = draft.Failure != null ? Texts.T("wizard.failed", draft.Failure)
                    : !draft.Loaded ? Texts.T("wizard.loading")
                    : draft.Rows.Count == 0 ? Texts.T("wizard.empty") : Texts.T("wizard.loaded", draft.Rows.Count);
                // Refresh even hidden materialized action text.
                draft.Reinclude.Content = Texts.T("wizard.reinclude", 0);
            }
        }
        finally { _applyingTexts = false; }
        foreach (var draft in _drafts) Refresh(draft);
        RefreshCommandHelp(_apply, "admission.apply", preserveHelp: true);
        // Reset controls are materialized by the list template, not owned by the
        // static label callbacks. Never overwrite their per-thread name/help bindings.
        foreach (var reset in this.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Name == "BoardAdmissionResetChoice"))
            RefreshCommandHelp(reset, "admission.reset-choice", preserveHelp: true);
        RefreshTotal();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing = true;
        Texts.Changed -= OnTextsChanged;
        _cancel.Cancel();
        _cancel.Dispose();
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Help(string text) => new() { Text = text, Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private static FuncDataTemplate<PreviewRow> MakeTemplate() => new((row, _) =>
    {
        if (row == null) return new TextBlock();
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(4, 8), DataContext = row };
        var title = Wrapped(string.Empty);
        title.Bind(TextBlock.TextProperty, new Binding(nameof(PreviewRow.Title)));
        var include = new CheckBox { Name = "BoardAdmissionInclude", Content = title };
        include.Bind(AutomationProperties.NameProperty, new Binding(nameof(PreviewRow.Title)));
        include.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(PreviewRow.Included)) { Mode = BindingMode.TwoWay });
        var reason = Help(row.Reason);
        reason.Bind(TextBlock.TextProperty, new Binding(nameof(PreviewRow.Reason)));
        var decision = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        reason.VerticalAlignment = VerticalAlignment.Center;
        decision.Children.Add(reason);
        var reset = new Button
        {
            Name = "BoardAdmissionResetChoice",
            Padding = new Thickness(8, 4), MinHeight = 32,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        }.CaptionText();
        reset.Bind(ContentControl.ContentProperty, new Binding(nameof(PreviewRow.ResetLabel)));
        reset.Bind(AutomationProperties.NameProperty, new Binding(nameof(PreviewRow.ResetName)));
        reset.Bind(AutomationProperties.HelpTextProperty, new Binding(nameof(PreviewRow.ResetHint)));
        // Initialize the tooltip at materialization time. The richer Name/HelpText
        // bindings remain owned by the row; the adapter only owns the tooltip.
        MeowChan.Desktop.Commands.CommandBindings.BindExisting(reset, "admission.reset-choice", preserveHelp: true);
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
        RefreshCommandHelp(remove, "admission.remove-rule");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(field); Grid.SetColumn(action, 1); grid.Children.Add(action); Grid.SetColumn(text, 2); grid.Children.Add(text); Grid.SetColumn(remove, 3); grid.Children.Add(remove);
        var editor = new RuleEditor(field, action, text, remove, grid); draft.Editors.Add(editor); draft.RulePanel.Children.Add(grid);
        field.SelectionChanged += (_, _) => Refresh(draft); action.SelectionChanged += (_, _) => Refresh(draft);
        text.TextChanged += (_, _) => Refresh(draft);
        remove.Click += (_, _) => { draft.Editors.Remove(editor); draft.RulePanel.Children.Remove(grid); Refresh(draft); };
        if (_apply != null) Refresh(draft);
    }
    private static void ApplyEditorTexts(RuleEditor editor)
    {
        // Capture semantic values before replacing choices; SelectionChanged must not
        // evaluate a partially refreshed editor or change any user-authored text.
        var field = ((Choice<ThreadTextField>)editor.Field.SelectedItem!).Value;
        var action = ((Choice<ThreadTextAction>)editor.Action.SelectedItem!).Value;
        var fields = new[] { new Choice<ThreadTextField>(ThreadTextField.Title, Texts.T("wizard.titleField")), new Choice<ThreadTextField>(ThreadTextField.OpeningPost, Texts.T("wizard.opField")), new Choice<ThreadTextField>(ThreadTextField.TitleAndOpeningPost, Texts.T("wizard.bothField")) };
        var actions = new[] { new Choice<ThreadTextAction>(ThreadTextAction.Include, Texts.T("wizard.include")), new Choice<ThreadTextAction>(ThreadTextAction.Exclude, Texts.T("wizard.exclude")) };
        editor.Field.ItemsSource = fields;
        editor.Field.SelectedItem = fields.First(item => item.Value == field);
        editor.Action.ItemsSource = actions;
        editor.Action.SelectedItem = actions.First(item => item.Value == action);
        editor.Text.PlaceholderText = Texts.T("wizard.contains");
        AutomationProperties.SetName(editor.Field, Texts.T("wizard.ruleField"));
        AutomationProperties.SetName(editor.Action, Texts.T("wizard.ruleAction"));
        AutomationProperties.SetName(editor.Text, Texts.T("wizard.ruleText"));
        AutomationProperties.SetName(editor.Remove, Texts.T("wizard.removeRule"));
        RefreshCommandHelp(editor.Remove, "admission.remove-rule");
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
        if (_applyingTexts || _closing || _disposed) return;
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
        draft.Failure = null;
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
                draft.Failure = exception.Message;
                draft.Status.Text = Texts.T("wizard.failed", draft.Failure);
                draft.Status.Classes.Set("workspace-error", true);
                draft.Retry.IsVisible = true;
                RefreshTotal();
            }
        }
        finally { draft.Loading = false; if (!_closing && !_disposed) draft.Retry.IsEnabled = true; }
    }
    private void Accept()
    {
        if (!CanApply) return;
        Close(new BoardAdmissionSelection(_drafts.ToDictionary(draft => draft.Url, Policy, StringComparer.Ordinal), _direct.IsChecked == true,
            _drafts.ToDictionary(draft => draft.Url, draft => draft.Reactivated.ToArray(), StringComparer.Ordinal)));
    }
}
