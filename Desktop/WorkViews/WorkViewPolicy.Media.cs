using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.7: media page header, filter fields and selection bar. Owned by WV5.
// Ids are generic on purpose (a control keeps its id when the filter surface is reworked): media.search, media.sort,
// media.filter.toggle, media.toolbar.refresh, … Direct/Required rows never move; Overflow rows sit under the header's or the
// selection bar's "More" (⋯), a filter field under "More filters". A filter field whose value is not the default (an
// active filter) is shown in every view (R6), so no view hides why the grid shows what it shows (R4).
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> MediaEntries() =>
    [
        // Header: finding, ordering and the filter toggle (with its badge) are the page's job in every view.
        Same("media.search", WorkViewPage.Media, WorkViewKind.Element, Required, "input.focus-media-search"),
        Same("media.filter.toggle", WorkViewPage.Media, WorkViewKind.Element, Required),
        Same("media.sort", WorkViewPage.Media, WorkViewKind.Element, Required),
        // The "More" (⋯) button itself: shown whenever the view keeps something under it.
        Same("media.toolbar.more", WorkViewPage.Media, WorkViewKind.Element, Required),
        new("media.toolbar.refresh", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 10),
        new("media.toolbar.shuffle", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 20),
        // Showing missing files is a maintenance view; switched on it is a state and stays on the bar (R6).
        new("media.toolbar.missing", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct, OverflowOrder: 30),
        new("media.toolbar.export-all", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Direct, OverflowOrder: 40),
        // After AP6 the saved-ratings manager lives under the media page's More in every view; its shortcut is routed by the shell.
        new("media.toolbar.manage-ratings", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow,
            "media.rating.manage", OverflowOrder: 50),
        // Filter fields: Barebones keeps name (above), type and favourites; Simple adds source, board, rating and tags;
        // Advanced adds sound, "without media tags" and the tag mode. The rest is under "More filters" (never lost).
        Same("media.filter.kind", WorkViewPage.Media, WorkViewKind.Element, Direct),
        Same("media.filter.favorites", WorkViewPage.Media, WorkViewKind.Element, Direct),
        new("media.filter.source", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.board", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.rating", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.tags", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.sound", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct),
        new("media.filter.without-media-tags", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct),
        new("media.filter.tag-mode", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct),
        // The toggle that opens the fields a view keeps back; shown while any is held back or while it is open.
        Same("media.filter.more", WorkViewPage.Media, WorkViewKind.Element, Required),
        // The "N without media tags" count is derived information; Barebones leaves it out (its filter is under More filters).
        new("media.status.untagged", WorkViewPage.Media, WorkViewKind.Element, Hidden, Direct, Direct,
            Alternative: "media.filter.without-media-tags"),
        // Selection bar: the count, the gallery and "Clear selection" are the selection itself (state, never reduced).
        Same("media.selection.status", WorkViewPage.Media, WorkViewKind.Element, Required),
        Same("media.selection.gallery", WorkViewPage.Media, WorkViewKind.Action, Required),
        Same("media.selection.clear", WorkViewPage.Media, WorkViewKind.Action, Required),
        Same("media.selection.more", WorkViewPage.Media, WorkViewKind.Element, Required),
        new("media.selection.tag", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 10),
        new("media.selection.export", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Direct, OverflowOrder: 20),
    ];
}
