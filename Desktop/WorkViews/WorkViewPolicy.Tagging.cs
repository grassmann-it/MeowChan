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
        // The key hint under the list is help text: Barebones leaves it out, the keys and the context menu stay.
        new("tagging.keys-hint", WorkViewPage.Tagging, WorkViewKind.Element, Hidden, Direct, Direct, Alternative: "tagging.continue-here"),
        new("tagging.toolbar.add", WorkViewPage.Tagging, WorkViewKind.Action, Direct, Direct, Direct, "tagging.add", OverflowOrder: 10),
        new("tagging.toolbar.clear-done", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Direct, Direct, "tagging.clear-done", OverflowOrder: 20),
        new("tagging.toolbar.clear", WorkViewPage.Tagging, WorkViewKind.Action, Overflow, Overflow, Direct, "tagging.clear", OverflowOrder: 30),
    ];
}
