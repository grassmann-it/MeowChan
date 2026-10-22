using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Filters;
using MeowChan.Rules;

namespace MeowChan.Desktop.Plugins;

/// <summary>Labels existing grant identities without parsing, replacing or broadening them.</summary>
internal static class PluginGrantPresentation
{
    internal static string ScopeLabel(MediaRuleScope scope) => scope.Kind switch
    {
        MediaRuleScopeKind.Global => Texts.T("rules.global"),
        MediaRuleScopeKind.Source => Texts.T("rules.source") + " · " + scope.Source,
        MediaRuleScopeKind.Board => Texts.T("rules.board") + " · " + scope.Source + "/" + scope.Board,
        _ => Texts.T("rules.thread") + " · " + scope.Source + "/" + scope.Board + "/" + scope.Thread
    };

    internal static string RuleLabel(string? ruleId, IEnumerable<MediaRuleScope> knownScopes)
    {
        if (ruleId == null) return Texts.T("plugins.allRules");
        MediaRuleScope? scope = knownScopes.Prepend(MediaRuleScope.Global)
            .FirstOrDefault(candidate => FilterRuleIdentity.ForScope(candidate) == ruleId);
        return scope == null ? Texts.T("plugins.unresolvedRule", ruleId) : ScopeLabel(scope);
    }

    internal static string Describe(PluginGrantSnapshot grant, IEnumerable<MediaRuleScope> knownScopes, bool effective) =>
        Texts.T(effective ? "plugins.grantOrigin" : "plugins.grantSummary", grant.Scope.SourceId,
            grant.Scope.BoardId ?? Texts.T("plugins.allBoards"), RuleLabel(grant.Scope.RuleId, knownScopes), grant.Access);
}
