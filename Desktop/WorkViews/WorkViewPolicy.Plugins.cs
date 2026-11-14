using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.10: plugins page. Owned by WV6. Approval and activation flows (enable, candidate choice, SDK transition,
// draft footer with its reasons, recovery) are never reduced; folders, adding files, watches and the developer area move
// under More or fold. The "More actions" menu of a plugin already carries Watches and developer details in every view.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> PluginsEntries() =>
    [
        Same("plugins.header.refresh", WorkViewPage.Plugins, WorkViewKind.Action, Direct, "plugins.refresh"),
        // Adding a file and opening the plugin folder: under More in Barebones and Simple (the add file is a checked copy).
        new("plugins.header.add", WorkViewPage.Plugins, WorkViewKind.Action, Overflow, Overflow, Direct, OverflowOrder: 10),
        new("plugins.header.folder", WorkViewPage.Plugins, WorkViewKind.Action, Overflow, Overflow, Direct,
            "plugins.folder", OverflowOrder: 20),
        // Repair review, inventory problems and the error banner appear when the state asks for them, in every view.
        Same("plugins.header.review-repairs", WorkViewPage.Plugins, WorkViewKind.Action, Required, safety: true),
        Same("plugins.header.inventory-problems", WorkViewPage.Plugins, WorkViewKind.Section, Required, safety: true),
        Same("plugins.header.error", WorkViewPage.Plugins, WorkViewKind.Element, Required, safety: true),
        Same("plugins.directory", WorkViewPage.Plugins, WorkViewKind.Section, Collapsed),
        // Approval and activation flows: Required, never moved.
        Same("plugins.detail.sdk-transition", WorkViewPage.Plugins, WorkViewKind.Action, Required, safety: true),
        Same("plugins.detail.draft-footer", WorkViewPage.Plugins, WorkViewKind.Element, Required, safety: true),
        // Watches: the "More actions" menu of the plugin lists them in every view, Barebones keeps only that entry.
        new("plugins.detail.watches", WorkViewPage.Plugins, WorkViewKind.Action, Overflow, Direct, Direct, "plugins.watches",
            OverflowOrder: 10),
        Same("plugins.detail.info", WorkViewPage.Plugins, WorkViewKind.Section, Collapsed),
        // Rights: folded in Barebones and Simple, open in Advanced. "View permissions" in More actions opens it in any view.
        new("plugins.detail.grants", WorkViewPage.Plugins, WorkViewKind.Section, Collapsed, Collapsed, Direct,
            "plugins.show-grants"),
        // Developer area: reached through "Show developer details" in More actions in Barebones; folded elsewhere.
        // A build error, candidate diagnostics or a probe result keep it on the page (R6).
        new("plugins.detail.developer", WorkViewPage.Plugins, WorkViewKind.Element, Overflow, Collapsed, Collapsed,
            "plugins.show-developer-tools", OverflowOrder: 20),
    ];
}
