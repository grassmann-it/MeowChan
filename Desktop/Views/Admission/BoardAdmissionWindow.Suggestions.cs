using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

internal sealed partial class BoardAdmissionWindow
{
    private sealed record SuggestionPanel(StackPanel View, StackPanel Rows, TextBlock Scanned)
    {
        public List<SuggestionEditor> Editors { get; } = new();
        public BoardTitleSuggestions? History { get; set; }
        public int[]? CatalogCounts { get; set; }
        public List<PreviewRow>? CountingRows { get; set; }
        public bool Synchronizing { get; set; }
    }

    private sealed record SuggestionEditor(BoardTitleSuggestion Pattern, CheckBox Check, TextBlock Details)
    {
        public RuleEditor? Editor { get; set; }
    }

    private SuggestionPanel CreateSuggestionPanel()
    {
        var title = Localized(Wrapped(string.Empty), control => control.Text = Texts.T("wizard.suggest.title"));
        title.Classes.Add("section-title");
        AutomationProperties.SetAutomationId(title, "BoardAdmissionSuggestionsTitle");
        var count = Help(string.Empty);
        AutomationProperties.SetAutomationId(count, "BoardAdmissionSuggestionsScanned");
        var heading = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, Children = { title, count } };
        var rows = new StackPanel { Spacing = 8 };
        var view = new StackPanel { IsVisible = false, Spacing = 8, Children = { heading,
            new Border { Classes = { "workspace-card" }, Child = rows } } };
        AutomationProperties.SetAutomationId(view, "BoardAdmissionSuggestions");
        return new(view, rows, count);
    }

    private WorkspaceBanner CreateNarrowingWarning(bool alreadyWatching)
    {
        string key = alreadyWatching ? "wizard.suggest.narrowWatching" : "wizard.suggest.narrow";
        var message = Localized(Wrapped(string.Empty), control =>
        {
            control.Text = Texts.T(key);
            AutomationProperties.SetName(control, WorkspaceBanner.AnnouncedText(NotificationType.Warning, control.Text));
        });
        AutomationProperties.SetAutomationId(message, "BoardAdmissionNarrowingWarning");
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        return new() { Child = message, Type = NotificationType.Warning, ShowSeverityIcon = true, IsVisible = false };
    }

    private async Task LoadSuggestionsAsync(Func<string, CancellationToken, Task<BoardTitleSuggestions>> suggest)
    {
        CancellationToken token = _cancel.Token;
        var counts = new List<Task>();
        foreach (Draft draft in _drafts)
        {
            if (_closing) break;
            try
            {
                BoardTitleSuggestions history = await suggest(draft.Url, token);
                if (_closing || history.Items.Count == 0 || draft.Suggestions is not { } panel) continue;
                panel.History = history;
                foreach (BoardTitleSuggestion pattern in history.Items) AddSuggestion(draft, panel, pattern);
                RefreshSuggestions(draft);
                panel.View.IsVisible = true;
                counts.Add(LoadSuggestionCatalogCountsAsync(draft));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { Log.Warning(error, "Could not read local title suggestions for {BoardUrl}", draft.Url); }
        }
        await Task.WhenAll(counts);
    }

    private void AddSuggestion(Draft draft, SuggestionPanel panel, BoardTitleSuggestion pattern)
    {
        var rule = Wrapped(pattern.RuleText);
        rule.Classes.Add("workspace-field-label");
        var example = Wrapped(pattern.ExampleTitle);
        example.MaxLines = 2;
        example.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(example, pattern.ExampleTitle);
        var details = Help(string.Empty);
        var label = new WrapPanel { ItemSpacing = 12, Children = { rule, example } };
        var content = new StackPanel { Spacing = 4, Children = { label, details } };
        var check = new CheckBox { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(check, "BoardAdmissionSuggestion." + pattern.RuleText);
        AutomationProperties.SetName(check, pattern.RuleText + " · " + pattern.ExampleTitle);
        AutomationProperties.SetAutomationId(details, "BoardAdmissionSuggestionDetails." + pattern.RuleText);
        var suggestion = new SuggestionEditor(pattern, check, details);
        panel.Editors.Add(suggestion);
        panel.Rows.Children.Add(check);
        check.IsCheckedChanged += (_, _) =>
        {
            if (panel.Synchronizing || _closing) return;
            if (check.IsChecked == true && suggestion.Editor == null)
                AddRule(draft, new(ThreadTextField.Title, ThreadTextAction.Include, pattern.RuleText), suggestion);
            else if (check.IsChecked != true && suggestion.Editor is { } editor)
                RemoveRule(draft, editor);
        };
    }

    private static bool MatchesSuggestion(ThreadTextRule rule, BoardTitleSuggestion suggestion) =>
        rule is { Field: ThreadTextField.Title, Action: ThreadTextAction.Include }
        && string.Equals(rule.Contains, suggestion.RuleText, StringComparison.OrdinalIgnoreCase);

    private async Task LoadSuggestionCatalogCountsAsync(Draft draft)
    {
        if (_closing || !draft.Loaded || draft.Suggestions is not { History: { } history, CatalogCounts: null } panel
            || panel.CountingRows == draft.Rows) return;
        var rows = draft.Rows;
        panel.CountingRows = rows;
        string[] titles = rows.Select(row => row.Thread.Title).ToArray();
        string[] rules = panel.Editors.Select(editor => editor.Pattern.RuleText).ToArray();
        CancellationToken token = _cancel.Token;
        try
        {
            int[] counts = await Task.Run(() => CountCatalogMatches(titles, rules, token), token);
            if (_closing || !draft.Loaded || draft.Rows != rows || panel.History != history) return;
            panel.CatalogCounts = counts;
            RefreshSuggestions(draft);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Log.Warning(error, "Could not count catalog matches for {BoardUrl}", draft.Url); }
        finally { if (panel.CountingRows == rows) panel.CountingRows = null; }
    }

    private static int[] CountCatalogMatches(string[] titles, string[] rules, CancellationToken token)
    {
        var counts = new int[rules.Length];
        foreach (string title in titles)
        {
            token.ThrowIfCancellationRequested();
            for (int index = 0; index < rules.Length; index++)
                if (title.Contains(rules[index], StringComparison.OrdinalIgnoreCase)) counts[index]++;
        }
        return counts;
    }

    private static void RefreshSuggestions(Draft draft)
    {
        bool hasNewInclude = !draft.Initial.Rules.Any(rule => rule.Action == ThreadTextAction.Include)
            && draft.Editors.Any(editor => ReadRule(editor) is { Action: ThreadTextAction.Include } rule && !string.IsNullOrWhiteSpace(rule.Contains));
        draft.NarrowingWarning.IsVisible = hasNewInclude;
        if (draft.Suggestions is not { History: { } history } panel) return;
        panel.Scanned.Text = Texts.Count("wizard.suggest.scanned", history.Scanned);
        var available = draft.Editors.ToHashSet();
        foreach (SuggestionEditor suggestion in panel.Editors)
            if (suggestion.Editor is { } editor && !available.Remove(editor)) suggestion.Editor = null;

        panel.Synchronizing = true;
        try
        {
            for (int index = 0; index < panel.Editors.Count; index++)
            {
                SuggestionEditor suggestion = panel.Editors[index];
                var pattern = suggestion.Pattern;
                if (suggestion.Editor == null && draft.Editors.FirstOrDefault(editor => available.Contains(editor)
                    && MatchesSuggestion(ReadRule(editor), pattern)) is { } matching)
                {
                    available.Remove(matching);
                    suggestion.Editor = matching;
                }
                suggestion.Check.IsChecked = suggestion.Editor != null;
                var details = new List<string> { Texts.Count("wizard.suggest.row", pattern.Count,
                    pattern.Count, ReaderDateText.Relative(pattern.LastSeenAt, history.AsOf)) };
                if (pattern.Rhythm > TimeSpan.Zero)
                {
                    var (value, key) = ReaderDateText.DurationParts(pattern.Rhythm);
                    details.Add(Texts.T("wizard.suggest.rhythm", Texts.T(key, value)));
                }
                if (panel.CatalogCounts is { } counts) details.Add(Texts.Count("wizard.suggest.inCatalog", counts[index]));
                if (suggestion.Editor is { } editor && !MatchesSuggestion(ReadRule(editor), pattern))
                    details.Add(Texts.T("wizard.suggest.edited"));
                suggestion.Details.Text = string.Join(" · ", details);
                AutomationProperties.SetHelpText(suggestion.Check, suggestion.Details.Text);
            }
        }
        finally { panel.Synchronizing = false; }
    }
}
