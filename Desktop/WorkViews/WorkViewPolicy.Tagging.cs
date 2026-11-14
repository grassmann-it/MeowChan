using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// PT2 (PLAN-POWER-TAGGEN §1.3): the tagging page. Progress, the start button, the queue list and its errors are the page itself
// and the same in every view; the stamp set and the state filter are working tools shown in every view as well (a filter that is
// not "All entries" is a state, R6). Only the toolbar thins out: removing the finished entries moves under More in Barebones,
// clearing the whole queue (destructive, rare) everywhere but Advanced. Every row command stays in the list's context menu.
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
        // T1: "Last shown: file · position n of m" under the progress; a link that continues exactly there. Barebones leaves the
        // sentence out: its start button already says "Continue at n of m" and carries the same action.
        new("tagging.last-shown", WorkViewPage.Tagging, WorkViewKind.Element, Hidden, Direct, Direct, Alternative: "tagging.start"),
        // The key hint under the list is help text: Barebones leaves it out, the keys and the context menu stay.
        new("tagging.keys-hint", WorkViewPage.Tagging, WorkViewKind.Element, Hidden, Direct, Direct, Alternative: "tagging.continue-here"),
        // TG1: the help button of the page sits at the end of the tool row; Barebones moves it under More.
        new("tagging.help", WorkViewPage.Tagging, WorkViewKind.Element, Overflow, Direct, Direct, OverflowOrder: 50),
        // TG1: "List | Grid" sits next to the filter; Barebones moves it under More (the saved view stays what it was).
        new("tagging.view-toggle", WorkViewPage.Tagging, WorkViewKind.Element, Overflow, Direct, Direct, OverflowOrder: 40),
        // TG1: the selection bar above the list or grid, shown while something is selected. The count, the way to tag and the cross
        // are the selection itself in every view; stamping, setting a state and removing thin out like the toolbar.
        Same("tagging.selection-bar", WorkViewPage.Tagging, WorkViewKind.Element, Required),
        Same("tagging.selection.status", WorkViewPage.Tagging, WorkViewKind.Element, Required),
        Same("tagging.selection.tag", WorkViewPage.Tagging, WorkViewKind.Action, Required),
        Same("tagging.selection.remove", WorkViewPage.Tagging, WorkViewKind.Action, Required),
        Same("tagging.selection.clear", WorkViewPage.Tagging, WorkViewKind.Action, Required),
        new("tagging.selection.stamp", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Direct, Direct, "tags.quick.stamp-apply", OverflowOrder: 10),
        new("tagging.selection.state", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Overflow, Direct, OverflowOrder: 20),
        new("tagging.toolbar.add", WorkViewPage.Tagging, WorkViewKind.Action, Direct, Direct, Direct, "tagging.add", OverflowOrder: 10),
        new("tagging.toolbar.clear-done", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Direct, Direct, "tagging.clear-done", OverflowOrder: 20),
        new("tagging.toolbar.clear", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Overflow, Direct, "tagging.clear", OverflowOrder: 30),
    ];
}
