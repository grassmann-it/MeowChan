using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Automation;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class RulesPage
{
    internal Func<IEnumerable<ArchiveBoardIdentity>>? GetSuggestionContexts { get; set; }
    private IdentitySuggestions? _ruleSuggestions;
    private bool _ruleSuggestionError;
    private void InitializeRuleSuggestions()
    {
        var source = this.FindControl<IdentitySuggestionBox>("NewRuleSourceInput")!;
        var board = this.FindControl<IdentitySuggestionBox>("NewRuleBoardInput")!;
        source.Candidates = (text, _) => Task.FromResult(RuleSuggestions(false, text));
        board.Candidates = (text, _) => Task.FromResult(RuleSuggestions(true, text));
        source.TextChanged += (_, _) => { board.Dismiss(); board.RefreshSuggestions(); };
        this.FindControl<Expander>("NewRuleScopeExpander")!.PropertyChanged += (_, e) =>
        { if (e.Property == Expander.IsExpandedProperty && e.NewValue is true) RebuildRuleSuggestions(); };
        this.FindControl<Button>("RuleRetrySuggestions")!.Click += (_, _) => { RebuildRuleSuggestions(); source.Focus(); source.RestartSuggestions(); };
    }
    private IdentitySuggestion[] RuleSuggestions(bool boards, string text)
    {
        if (_ended) return Array.Empty<IdentitySuggestion>();
        if (_ruleSuggestions == null && !_ruleSuggestionError) RebuildRuleSuggestions();
        return _ruleSuggestions?.Match(boards, text, NewSourceId?.Trim()) ?? Array.Empty<IdentitySuggestion>();
    }
    private void RebuildRuleSuggestions()
    {
        if (_ended) return;
        AutomationProperties.SetName(this.FindControl<IdentitySuggestionBox>("NewRuleSourceInput")!, SourceIdLabel);
        AutomationProperties.SetName(this.FindControl<IdentitySuggestionBox>("NewRuleBoardInput")!, BoardIdLabel);
        try
        {
            var preferred = _confirmed.Rules.Select(rule => rule.Scope).Concat(_drafts.Keys)
                .Where(scope => scope.Source != null).Select(scope => new ArchiveBoardIdentity(scope.Source!, scope.Board ?? ""));
            _ruleSuggestions = IdentitySuggestions.FromContexts(preferred,
                GetSuggestionContexts?.Invoke() ?? Array.Empty<ArchiveBoardIdentity>(), Array.Empty<string>());
            _ruleSuggestionError = false;
        }
        catch { _ruleSuggestions = null; _ruleSuggestionError = true; }
        this.FindControl<TextBlock>("RuleSuggestionsHint")!.Text = Texts.T(_ruleSuggestionError ? "suggestions.unavailable" : "suggestions.limited");
        this.FindControl<Button>("RuleRetrySuggestions")!.Content = Texts.T("suggestions.retry");
        this.FindControl<Button>("RuleRetrySuggestions")!.IsVisible = _ruleSuggestionError;
        this.FindControl<IdentitySuggestionBox>("NewRuleSourceInput")!.Dismiss();
        this.FindControl<IdentitySuggestionBox>("NewRuleBoardInput")!.Dismiss();
    }
    private void EndRuleSuggestions()
    {
        _ruleSuggestions = null; GetSuggestionContexts = null;
        this.FindControl<IdentitySuggestionBox>("NewRuleSourceInput")!.EndSuggestions();
        this.FindControl<IdentitySuggestionBox>("NewRuleBoardInput")!.EndSuggestions();
    }
}
