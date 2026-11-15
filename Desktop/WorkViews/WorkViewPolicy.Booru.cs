using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// Work-view contract for the hosted Danbooru workbench. The page keeps its complete search,
/// result, watcher and local-catalog surface in every mode; only non-essential result actions move
/// into the workbench's existing More surfaces. This is policy only: the hosted view remains the
/// owner of all provider, queue and watcher actions.
/// </summary>
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> BooruEntries() =>
    [
        // Search is the page's primary task and must never disappear behind a presentation mode.
        Same("booru.search.input", WorkViewPage.Booru, WorkViewKind.Element, Required),
        Same("booru.search.mode", WorkViewPage.Booru, WorkViewKind.Element, Required),
        Same("booru.search.submit", WorkViewPage.Booru, WorkViewKind.Action, Required),
        Same("booru.search.cancel", WorkViewPage.Booru, WorkViewKind.Action, Required),
        Same("booru.search.status", WorkViewPage.Booru, WorkViewKind.Element, Required, safety: true),
        Same("booru.results", WorkViewPage.Booru, WorkViewKind.Element, Required, safety: true),
        new("booru.result.load", WorkViewPage.Booru, WorkViewKind.Action, Direct, Direct, Direct),
        new("booru.result.cancel", WorkViewPage.Booru, WorkViewKind.Action, Direct, Direct, Direct),
        new("booru.result.retry", WorkViewPage.Booru, WorkViewKind.Action, Direct, Direct, Direct),
        new("booru.result.open", WorkViewPage.Booru, WorkViewKind.Action, Overflow, Direct, Direct),
        new("booru.result.copy-link", WorkViewPage.Booru, WorkViewKind.Action, Overflow, Direct, Direct),
        Same("booru.result.details", WorkViewPage.Booru, WorkViewKind.Element, Direct),
        Same("booru.watchers.list", WorkViewPage.Booru, WorkViewKind.Element, Required, safety: true),
        Same("booru.watcher.check-now", WorkViewPage.Booru, WorkViewKind.Action, Direct),
        Same("booru.watcher.pause", WorkViewPage.Booru, WorkViewKind.Action, Direct),
        Same("booru.watcher.remove", WorkViewPage.Booru, WorkViewKind.Action, Direct),
        new("booru.watcher.restart", WorkViewPage.Booru, WorkViewKind.Action, Overflow, Overflow, Direct),
        new("booru.catalog.usage", WorkViewPage.Booru, WorkViewKind.Element, Overflow, Overflow, Direct),
        Same("booru.notice", WorkViewPage.Booru, WorkViewKind.Element, Required, safety: true),
    ];
}
