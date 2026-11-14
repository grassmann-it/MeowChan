using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.1/§1.2: title bar, header values/graph and the footer. Owned by WV1 after the foundation.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> ShellEntries() =>
    [
        // R5/R8: the view switch, pause state and the header values are never reduced.
        Same("shell.footer.view-switch", WorkViewPage.Shell, WorkViewKind.Element, Required, safety: true),
        Same("shell.header.pause", WorkViewPage.Shell, WorkViewKind.Action, Required, "session.pause", safety: true),
        Same("shell.header.app-menu", WorkViewPage.Shell, WorkViewKind.Action, Required, "navigation.app-menu", safety: true),
        Same("shell.header.net-value", WorkViewPage.Shell, WorkViewKind.Derived, Required),
        // "Network" setting still hides the disk value in every view (IoStatsDisplay.HeaderShowsDiskValue).
        Same("shell.header.disk-value", WorkViewPage.Shell, WorkViewKind.Derived, Required),
        // Graph rows for the "Auto" header-graph setting; an explicit setting wins (IoStatsDisplay.HeaderShowsDisk).
        Same("shell.header.net-graph", WorkViewPage.Shell, WorkViewKind.Derived, Direct),
        new("shell.header.disk-graph", WorkViewPage.Shell, WorkViewKind.Derived, Hidden, Hidden, Direct,
            Alternative: "shell.footer.statistics"),
        // Footer destinations: Downloads, Settings and Tools ("More") always; the rest direct by view, else in Tools.
        Same("shell.footer.downloads", WorkViewPage.Shell, WorkViewKind.Destination, Required, "navigation.downloads"),
        Same("shell.footer.settings", WorkViewPage.Shell, WorkViewKind.Destination, Required, "navigation.settings"),
        Same("shell.footer.tools-menu", WorkViewPage.Shell, WorkViewKind.Destination, Required, "navigation.tools-menu"),
        Footer("archive", Overflow, Direct, Direct),
        Footer("media", Overflow, Direct, Direct),
        Footer("rules", Overflow, Overflow, Direct),
        Footer("sources", Overflow, Overflow, Direct),
        Footer("diagnostics", Overflow, Overflow, Direct),
        Footer("plugins", Overflow, Overflow, Direct),
        Footer("statistics", Overflow, Overflow, Direct),
    ];

    private static WorkViewEntry Footer(string page, Presence barebones, Presence simple, Presence advanced) =>
        new("shell.footer." + page, WorkViewPage.Shell, WorkViewKind.Destination, barebones, simple, advanced, "navigation." + page);
}
