using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.8: rules page. Owned by WV6. The scope header, the editable fields, validation and Save/Discard are never
// reduced; the optional blocks fold or move under the page's "More" (⋯) and every one keeps a way back (R1, R6).
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> RulesEntries() =>
    [
        // "Where am I": the heading of the edited scope names it in every view (the breadcrumb below is a longer form of it).
        Same("rules.scope.editing-header", WorkViewPage.Rules, WorkViewKind.Element, Required, safety: true),
        // "Choose another scope" is folded in every view (as before).
        Same("rules.scope.new", WorkViewPage.Rules, WorkViewKind.Section, Collapsed),
        // Breadcrumb with its parent link: Barebones reaches the parent scope under More ("Open parent scope").
        new("rules.scope.breadcrumb", WorkViewPage.Rules, WorkViewKind.Element, Overflow, Direct, Direct,
            "rules.open-parent", OverflowOrder: 10),
        // The plugin filter block folds in Barebones; an active filter is always open (R6) with its state in the header.
        new("rules.plugin-filter", WorkViewPage.Rules, WorkViewKind.Section, Collapsed, Direct, Direct),
        // Templates: under More in Barebones (their shortcuts rules.template-* keep working), folded in Simple.
        new("rules.templates", WorkViewPage.Rules, WorkViewKind.Element, Overflow, Collapsed, Direct, OverflowOrder: 40),
        // "Effective after saving": folded in Barebones; a validation error opens it (R6).
        new("rules.preview", WorkViewPage.Rules, WorkViewKind.Section, Collapsed, Direct, Direct),
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
