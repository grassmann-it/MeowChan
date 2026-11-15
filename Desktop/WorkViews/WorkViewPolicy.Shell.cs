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
            Alternative: "shell.footer.status"),
        // Header elements (plan §1.1). Neutral rows: the tagline, the graph block (values included) and the title-bar buttons are the
        // same in every view. The header graph's own layout (setting and window width) never reads a work view; these rows only
        // register the elements so that a later change of one row is a single edit (WorkViewGate.Apply is already wired).
        Same("shell.header.tagline", WorkViewPage.Shell, WorkViewKind.Element, Direct),
        Same("shell.header.graph", WorkViewPage.Shell, WorkViewKind.Element, Required),
        Same("shell.header.fullscreen", WorkViewPage.Shell, WorkViewKind.Action, Direct, "window.fullscreen"),
        Same("shell.header.theme", WorkViewPage.Shell, WorkViewKind.Action, Direct, "appearance.theme"),
        // "Continue reading" shows only while a reading position exists (state); the view does not reduce it. The plan's
        // "K in Barebones" is not applied: the AP13 header-book smokes require the action in every view.
        Same("shell.header.continue-reading", WorkViewPage.Shell, WorkViewKind.Action, Direct, "navigation.continue-reading"),
        // R3/R8: the app menu is complete in every view; the view radio group, the cycle command, theme and fullscreen live there.
        Same("shell.appmenu.view-group", WorkViewPage.Shell, WorkViewKind.Element, Required, safety: true),
        Same("shell.appmenu.view-cycle", WorkViewPage.Shell, WorkViewKind.Action, Required, "workspace.view-cycle", safety: true),
        Same("shell.appmenu.theme", WorkViewPage.Shell, WorkViewKind.Action, Required, "appearance.theme"),
        Same("shell.appmenu.fullscreen", WorkViewPage.Shell, WorkViewKind.Action, Required, "window.fullscreen"),
        // Footer destinations: Downloads, Settings and Tools ("More") always; the rest direct by view, else in Tools.
        Same("shell.footer.downloads", WorkViewPage.Shell, WorkViewKind.Destination, Required, "navigation.downloads"),
        Same("shell.footer.settings", WorkViewPage.Shell, WorkViewKind.Destination, Required, "navigation.settings"),
        Same("shell.footer.tools-menu", WorkViewPage.Shell, WorkViewKind.Destination, Required, "navigation.tools-menu"),
        Footer("archive", Overflow, Direct, Direct),
        Footer("media", Overflow, Direct, Direct),
        // Tagging follows Media's rule: a reading-side tool, so Barebones keeps it under More and Simple and Advanced show it.
        Footer("tagging", Overflow, Direct, Direct),
        Footer("booru", Overflow, Direct, Direct),
        Footer("rules", Overflow, Overflow, Direct),
        Footer("sources", Overflow, Overflow, Direct),
        // One Status page with the segments Statistics and Diagnostics (status.segments); the segments are no footer destinations.
        Footer("status", Overflow, Overflow, Direct),
    ];

    private static WorkViewEntry Footer(string page, Presence barebones, Presence simple, Presence advanced) =>
        new("shell.footer." + page, WorkViewPage.Shell, WorkViewKind.Destination, barebones, simple, advanced, "navigation." + page);
}
