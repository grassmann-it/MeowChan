using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.11: diagnostics page. Owned by WV7. The table, its details, the errors and the filters' default
// values are the same in every view (R4/R5); a view only decides where the filters and the two secondary actions sit.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> DiagnosticsEntries() =>
    [
        // Data and state: never reduced.
        Same("diagnostics.table", WorkViewPage.Diagnostics, WorkViewKind.Element, Required),
        Same("diagnostics.details", WorkViewPage.Diagnostics, WorkViewKind.Element, Required),
        Same("diagnostics.filter-summary", WorkViewPage.Diagnostics, WorkViewKind.Element, Required),
        Same("diagnostics.error", WorkViewPage.Diagnostics, WorkViewKind.Element, Required, safety: true),
        // Filters (source, text, problems only, include waiting, clear): closed in Barebones; a filter that differs from its
        // default opens the group (R6), so "Clear filters" is always reachable when it matters.
        new("diagnostics.filters", WorkViewPage.Diagnostics, WorkViewKind.Section, Collapsed, Direct, Direct),
        // Error path (J9): check now, retry the selection and show in downloads stay on the bar in every view.
        Same("diagnostics.toolbar.check-now", WorkViewPage.Diagnostics, WorkViewKind.Action, Required, "watch.check"),
        Same("diagnostics.toolbar.retry-selection", WorkViewPage.Diagnostics, WorkViewKind.Action, Required, "diagnostics.retry-selection"),
        Same("diagnostics.toolbar.show-in-downloads", WorkViewPage.Diagnostics, WorkViewKind.Action, Required, "watch.show-in-downloads"),
        // Bulk action and copy: under More where the view says so (the More button itself is derived from these rows).
        new("diagnostics.toolbar.retry-filtered", WorkViewPage.Diagnostics, WorkViewKind.Action, Overflow, Overflow, Direct,
            "diagnostics.retry-filtered", OverflowOrder: 20),
        new("diagnostics.toolbar.copy", WorkViewPage.Diagnostics, WorkViewKind.Action, Overflow, Direct, Direct,
            "diagnostics.copy", OverflowOrder: 10),
        Same("diagnostics.toolbar.more", WorkViewPage.Diagnostics, WorkViewKind.Element, Direct),
        // Platform facts (a few rows about this machine): their own group below the table, closed in every view.
        Same("diagnostics.platform", WorkViewPage.Diagnostics, WorkViewKind.Section, Collapsed),
        // Session messages: header with its counter is always there, the list starts closed like today.
        Same("diagnostics.session-messages", WorkViewPage.Diagnostics, WorkViewKind.Section, Collapsed),
    ];
}
