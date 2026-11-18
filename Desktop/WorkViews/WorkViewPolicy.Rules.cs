using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.8: rules page. Owned by WV6. The scope header, the editable fields, validation and Save/Discard are never
// reduced; the optional blocks fold or move under the page's "More" (⋯) and every one keeps a way back (R1, R6).
// Overview first (14.11.2026): the page opens on the list of saved rules, drafts and built-in defaults; Create/Edit open the
// in-page wizard (1 scope, 2 settings, 3 review). Wizard navigation, Close and the drafts list are never reduced.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> RulesEntries() =>
    [
        // Overview: the rule list is the page's content; Create starts the wizard; unsaved drafts are always discoverable (R5).
        Same("rules.overview", WorkViewPage.Rules, WorkViewKind.Element, Required),
        Same("rules.overview.create", WorkViewPage.Rules, WorkViewKind.Action, Required),
        Same("rules.overview.drafts", WorkViewPage.Rules, WorkViewKind.Element, Required, safety: true),
        // The read-only built-in baseline every rule inherits from.
        Same("rules.overview.defaults", WorkViewPage.Rules, WorkViewKind.Element, Direct),
        // "Where am I": the heading of the edited scope names it in every view (the breadcrumb below is a longer form of it).
        Same("rules.scope.editing-header", WorkViewPage.Rules, WorkViewKind.Element, Required, safety: true),
        // Wizard navigation: the step chips, Back/Next in the footer and Close at the header's right edge.
        Same("rules.wizard.steps", WorkViewPage.Rules, WorkViewKind.Element, Direct),
        Same("rules.wizard.close", WorkViewPage.Rules, WorkViewKind.Action, Required),
        Same("rules.wizard.back", WorkViewPage.Rules, WorkViewKind.Action, Required),
        Same("rules.wizard.next", WorkViewPage.Rules, WorkViewKind.Action, Required),
        // Step 1: the scope fields are the step's content; opening a saved rule or draft from there is folded (the overview lists them).
        Same("rules.scope.new", WorkViewPage.Rules, WorkViewKind.Element, Required),
        Same("rules.scope.saved", WorkViewPage.Rules, WorkViewKind.Section, Collapsed),
        // Breadcrumb with its parent link: Barebones reaches the parent scope under More ("Open parent scope").
        new("rules.scope.breadcrumb", WorkViewPage.Rules, WorkViewKind.Element, Overflow, Direct, Direct,
            "rules.open-parent", OverflowOrder: 10),
        // The plugin filter block folds in Barebones; an active filter is always open (R6) with its state in the header.
        new("rules.plugin-filter", WorkViewPage.Rules, WorkViewKind.Section, Collapsed, Direct, Direct),
        // Templates: an optional helper of step 2, folded by default; under More in Barebones (their shortcuts rules.template-* keep working).
        new("rules.templates", WorkViewPage.Rules, WorkViewKind.Element, Overflow, Collapsed, Collapsed, OverflowOrder: 40),
        // Step 3 "Effective after saving" is the review step's content; a validation error marks the step (R6).
        Same("rules.preview", WorkViewPage.Rules, WorkViewKind.Element, Required),
        // Open the rule an effective value comes from: the origin text "inherited from …" stays in every view.
        new("rules.preview.origin-types", WorkViewPage.Rules, WorkViewKind.Action, Overflow, Overflow, Direct,
            "rules.open-origin", OverflowOrder: 20),
        new("rules.preview.origin-size", WorkViewPage.Rules, WorkViewKind.Action, Overflow, Overflow, Direct,
            "rules.open-origin", OverflowOrder: 30),
        // A refused save or an invalid field is never folded (R5).
        Same("rules.preview.blocked", WorkViewPage.Rules, WorkViewKind.Element, Required, safety: true),
        Same("rules.footer.discard", WorkViewPage.Rules, WorkViewKind.Action, Required, "rules.discard"),
        Same("rules.footer.save", WorkViewPage.Rules, WorkViewKind.Action, Required, "rules.save"),
    ];
}
