using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// PT2 (PLAN-POWER-TAGGEN §1.3): the tagging page. Progress, the start button, the queue list and its errors are the page itself
// and the same in every view; the stamp set and the state filter are working tools shown in every view as well (a filter that is
// not "All entries" is a state, R6). Only the toolbar thins out: TQ2 moved removing the finished entries and clearing the whole queue (destructive,
// rare) under More in every view, so no destructive action stands beside "Add…". Every row command stays in the list's context menu.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> TaggingEntries() =>
    [
        Same("tagging.progress", WorkViewPage.Tagging, WorkViewKind.Element, Required),
        Same("tagging.start", WorkViewPage.Tagging, WorkViewKind.Action, Required, "tagging.start"),
        Same("tagging.list", WorkViewPage.Tagging, WorkViewKind.Element, Required),
        Same("tagging.error", WorkViewPage.Tagging, WorkViewKind.Element, Required, safety: true),
        Same("tagging.clear-confirm", WorkViewPage.Tagging, WorkViewKind.Element, Required, safety: true),
        Same("tagging.stamp", WorkViewPage.Tagging, WorkViewKind.Element, Direct),
        Same("tagging.filter", WorkViewPage.Tagging, WorkViewKind.Element, Direct),
        Same("tagging.group", WorkViewPage.Tagging, WorkViewKind.Element, Direct),
        new("tagging.columns", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Direct, Direct, "tagging.preview-column", OverflowOrder: 35),
        // The shortcut reference lives in Help in every view, not in a separately gated footer row.
        // TG1: the help button of the page sits at the end of the tool row; Barebones moves it under More.
        new("tagging.help", WorkViewPage.Tagging, WorkViewKind.Element, Overflow, Direct, Direct, OverflowOrder: 50),
        // TG1: "List | Grid" sits next to the filter; Barebones moves it under More (the saved view stays what it was).
        new("tagging.view-toggle", WorkViewPage.Tagging, WorkViewKind.Element, Overflow, Direct, Direct, OverflowOrder: 40),
        // The same actions are reachable at the same place in all three work views.
        Same("tagging.selection-bar", WorkViewPage.Tagging, WorkViewKind.Element, Required),
        Same("tagging.selection.status", WorkViewPage.Tagging, WorkViewKind.Element, Required),
        Same("tagging.selection.tag", WorkViewPage.Tagging, WorkViewKind.Action, Required),
        Same("tagging.selection.done", WorkViewPage.Tagging, WorkViewKind.Action, Required),
        Same("tagging.selection.more", WorkViewPage.Tagging, WorkViewKind.Action, Required),
        Same("tagging.selection.clear", WorkViewPage.Tagging, WorkViewKind.Action, Required),
        new("tagging.toolbar.add", WorkViewPage.Tagging, WorkViewKind.Action, Direct, Direct, Direct, "tagging.add", OverflowOrder: 10),
        new("tagging.toolbar.clear-done", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Overflow, Overflow, "tagging.clear-done", OverflowOrder: 20),
        new("tagging.toolbar.clear", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Overflow, Overflow, "tagging.clear", OverflowOrder: 30),
    ];
}
