using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.10: plugins page. Owned by WV6. Approval and activation flows (enable, candidate choice, SDK transition,
// draft footer with its reasons, recovery) are never reduced; refresh, folders, watches and the developer area move
// under More or fold. Adding a plugin remains direct. The plugin's More menu carries Watches and developer details in every view.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> PluginsEntries() =>
    [
        new("plugins.header.refresh", WorkViewPage.Sources, WorkViewKind.Action, Overflow, Overflow, Overflow,
            "plugins.refresh", OverflowOrder: 10),
        Same("plugins.header.add", WorkViewPage.Sources, WorkViewKind.Action, Direct),
        new("plugins.header.folder", WorkViewPage.Sources, WorkViewKind.Action, Overflow, Overflow, Overflow,
            "plugins.folder", OverflowOrder: 20),
        // Repair review, inventory problems and the error banner appear when the state asks for them, in every view.
        Same("plugins.header.review-repairs", WorkViewPage.Sources, WorkViewKind.Action, Required, safety: true),
        Same("plugins.header.inventory-problems", WorkViewPage.Sources, WorkViewKind.Section, Required, safety: true),
        Same("plugins.header.error", WorkViewPage.Sources, WorkViewKind.Element, Required, safety: true),
        // Approval and activation flows: Required, never moved.
        Same("plugins.detail.sdk-transition", WorkViewPage.Sources, WorkViewKind.Action, Required, safety: true),
        Same("plugins.detail.draft-footer", WorkViewPage.Sources, WorkViewKind.Element, Required, safety: true),
        // Watches: the "More actions" menu of the plugin lists them in every view, Barebones keeps only that entry.
        new("plugins.detail.watches", WorkViewPage.Sources, WorkViewKind.Action, Overflow, Direct, Direct, "plugins.watches",
            OverflowOrder: 10),
        Same("plugins.detail.info", WorkViewPage.Sources, WorkViewKind.Section, Collapsed),
        // Rights: folded in Barebones and Simple, open in Advanced. "View permissions" in More actions opens it in any view.
        new("plugins.detail.grants", WorkViewPage.Sources, WorkViewKind.Section, Collapsed, Collapsed, Direct,
            "plugins.show-grants"),
        // Developer area: reached through "Show developer details" in More actions in Barebones; folded elsewhere.
        // A build error, candidate diagnostics or a probe result keep it on the page (R6).
        new("plugins.detail.developer", WorkViewPage.Sources, WorkViewKind.Element, Overflow, Collapsed, Collapsed,
            "plugins.show-developer-tools", OverflowOrder: 20),
    ];
}
