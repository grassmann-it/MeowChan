using System;
using System.Linq;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan;

/// <summary>
/// The context menu of a row of the Rules overview (a saved rule or a draft): the actions of the row's buttons as catalog commands, the
/// parent scope, the summary as text, and the destructive entry last (Delete for a saved rule, Discard for a draft). Pure composition: the
/// page supplies what each entry does, so the same menu is built for the pointer and for the Menu key.
/// </summary>
internal static class RulesRowMenu
{
    /// <summary>The ids of the entries (stable for tests and smokes).</summary>
    internal static class Ids
    {
        internal const string Edit = "rules.open-scope";
        internal const string Parent = "rules.open-parent";
        internal const string Toggle = "rules.toggle-enabled";
        internal const string Copy = "rules.copy-summary";
        internal const string Delete = "rules.delete";
        internal const string Discard = "rules.discard";
    }

    /// <summary>The row as a sentence: its scope and what the rule sets, the same words the row shows.</summary>
    internal static string Summary(RuleOverviewRow row) =>
        row.ScopeLabel + ": " + string.Join(", ", row.Chips.Select(chip => chip.Text));

    /// <param name="row">The overview row the menu is for.</param>
    /// <param name="isDraftRow">The row is an unsaved draft without a saved rule (its last entry discards it).</param>
    /// <param name="parent">The scope above this one (null for the global scope) and its display name.</param>
    /// <param name="editable">False while the page is busy or has no rule store; the actions are then listed and say why they are off.</param>
    /// <param name="unavailableReason">The sentence shown for an action that is off.</param>
    internal static ContextMenuBuilder Compose(RuleOverviewRow row, bool isDraftRow, MediaRuleScope? parent, string? parentLabel, bool editable,
        string? unavailableReason, Action edit, Action openParent, Action toggle, Action copySummary, Action removeOrDiscard)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(openParent);
        ArgumentNullException.ThrowIfNull(toggle);
        ArgumentNullException.ThrowIfNull(copySummary);
        ArgumentNullException.ThrowIfNull(removeOrDiscard);
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("nav.rules") + ": " + row.ScopeLabel)
            .Command(Ids.Edit, edit, editable, editable ? null : unavailableReason, Texts.T(row.HasDraft ? "rules.overview.continue" : "rules.overview.edit"));
        if (parent != null)
            menu.Command(Ids.Parent, openParent, editable, editable ? null : unavailableReason,
                CommandCatalogLabel(Ids.Parent) + " · " + (parentLabel ?? string.Empty));
        if (row.Saved && !row.HasDraft)
            menu.Section().Command(Ids.Toggle, toggle, editable, editable ? null : unavailableReason,
                Texts.T(row.Enabled ? "rules.overview.disable" : "rules.overview.enable"));
        menu.Section().Command(Ids.Copy, copySummary);
        menu.Section();
        return isDraftRow
            ? menu.Command(Ids.Discard, removeOrDiscard, editable, editable ? null : unavailableReason, Texts.T("rules.discard"))
            : menu.Command(Ids.Delete, removeOrDiscard, editable, editable ? null : unavailableReason);
    }

    private static string CommandCatalogLabel(string id) => MeowChan.Desktop.Commands.CommandCatalog.Label(id);
}
