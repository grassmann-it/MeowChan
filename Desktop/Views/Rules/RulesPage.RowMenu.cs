using System;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan;

// The context menu of the overview rows (right-click, Menu key, Shift+F10 on a row or on one of its buttons). It offers what the row's
// buttons do through the same page methods (Edit/Continue, Enable/Disable, Delete/Discard with their confirmation), plus the parent scope
// and the row as text. The row cards are rebuilt when the overview changes, so a menu is always composed for the row as it is shown.
public partial class RulesPage
{
    private ContextMenuHost AttachOverviewRowMenu(Border card, RuleOverviewRow row, bool draftRow, Action removeOrDiscard)
    {
        return new ContextMenuHost(card, () => ComposeOverviewRowMenu(card, row, draftRow, removeOrDiscard), fallbackFocus: () => OverviewCreateButton.Focus());
    }

    /// <summary>The menu of a row as the page would open it now (tests and smokes).</summary>
    internal ContextMenuBuilder? ComposeOverviewRowMenu(Control owner, RuleOverviewRow row, bool draftRow, Action removeOrDiscard)
    {
        if (_ended || _wizard.IsOpen) return null;
        MediaRuleScope scope = row.Scope;
        MediaRuleScope? parent = ParentOf(scope);
        string? reason = IsEditable ? null : Texts.T(_busy ? "rules.saving" : "rules.unavailable");
        return RulesRowMenu.Compose(row, draftRow, parent, parent == null ? null : FormatScope(parent), IsEditable, reason,
            edit: () => OpenRuleFromOverview(scope),
            openParent: () => { if (parent != null) OpenRuleFromOverview(parent); },
            toggle: () => _ = ToggleRuleAsync(scope),
            copySummary: () => _ = CopyOverviewSummaryAsync(owner, RulesRowMenu.Summary(row)),
            removeOrDiscard: () => { if (IsEditable) removeOrDiscard(); });
    }

    private async System.Threading.Tasks.Task CopyOverviewSummaryAsync(Control owner, string summary)
    {
        if (await MenuClipboard.CopyAsync(owner, summary, _ => { })) return;
        if (_ended) return;
        _operationErrorKey = "ctxmenu.copyFailed";
        _operationErrorDetail = null;
        RaiseAll();
    }

    /// <summary>The scope one step up (thread to board to source to global); null for the global scope.</summary>
    internal static MediaRuleScope? ParentOf(MediaRuleScope scope) => scope.Kind switch
    {
        MediaRuleScopeKind.Thread => MediaRuleScope.ForBoard(scope.Source!, scope.Board!),
        MediaRuleScopeKind.Board => MediaRuleScope.ForSource(scope.Source!),
        MediaRuleScopeKind.Source => MediaRuleScope.Global,
        _ => null
    };
}
