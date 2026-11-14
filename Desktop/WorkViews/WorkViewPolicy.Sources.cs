using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.9: sources page. Owned by WV6. The source list and its state, the provider-mismatch consent, the observed
// threads and every error stay in all views; capability cards fold in Barebones and the less common actions move under More.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> SourcesEntries() =>
    [
        Same("sources.detail.website", WorkViewPage.Sources, WorkViewKind.Action, Direct, "location.open-source"),
        Same("sources.detail.add-downloads", WorkViewPage.Sources, WorkViewKind.Action, Direct, "input.focus-url"),
        // Consent: the provider-mismatch permission is never folded or moved (R5).
        Same("sources.detail.media-integrity", WorkViewPage.Sources, WorkViewKind.Element, Required, safety: true),
        // Capability cards: folded in Barebones; "Capture is (not) available" below them stays visible.
        new("sources.detail.capabilities", WorkViewPage.Sources, WorkViewKind.Section, Collapsed, Direct, Direct),
        // Technical details were folded in every view before.
        Same("sources.detail.technical", WorkViewPage.Sources, WorkViewKind.Section, Collapsed),
        new("sources.detail.rules", WorkViewPage.Sources, WorkViewKind.Action, Overflow, Direct, Direct,
            "rules.edit-scope", OverflowOrder: 10),
        new("sources.detail.diagnostics", WorkViewPage.Sources, WorkViewKind.Action, Overflow, Direct, Direct,
            "navigation.diagnostics", OverflowOrder: 20),
        new("sources.detail.plugin", WorkViewPage.Sources, WorkViewKind.Action, Overflow, Overflow, Direct, OverflowOrder: 30),
        // Observed threads with their "Open" and "All" actions are data and the way to them (R4).
        Same("sources.detail.watches", WorkViewPage.Sources, WorkViewKind.Element, Required, safety: true),
        Same("sources.detail.error", WorkViewPage.Sources, WorkViewKind.Element, Required, safety: true),
    ];
}
